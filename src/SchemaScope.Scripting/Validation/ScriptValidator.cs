using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Dom = Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SchemaScope.Scripting;

/// <summary>
/// Offline validation of everything the run wrote (DESIGN.md §5): GO-split + ScriptDom parse of every
/// .sql, <see cref="ScriptText.EvaluateExecBlock"/> round trips (as written and after an SSMS CRLF rewrite),
/// raw-module last-batch checks, 04 widen-only checks and 09 old-name checks. Never touches a database.
/// Writes <c>99_review\validation.md</c> through <see cref="ScriptContext.Review"/>.
/// </summary>
/// <remarks>
/// The Python checked syntax with <c>SET PARSEONLY ON</c> and the exec blocks with <c>SELECT @sql</c> on the
/// target. Both sent generated text to a server, which rule §1.1 forbids, so here the parse is ScriptDom's
/// SQL Server 2022 grammar and the exec blocks are evaluated by <see cref="ScriptText.TryEvaluateExecBlock"/>.
/// Expected module text is taken from the catalog (source, or target for rollbacks and 02 recreates), not only
/// from what the generator recorded, so a generator that hands the writer the wrong text is caught too.
/// </remarks>
public sealed partial class ScriptValidator
{
    /// <summary>The report file inside <see cref="ReviewLog.Folder"/>.</summary>
    public const string ReportFile = "validation.md";

    private const string ExecMarker = "DECLARE @sql nvarchar(max)";
    private const string ColumnTypesFolder = "04_columntypes";
    private const string RenamesFolder = "09_formatting_tables";
    // the API response and the result panel stay small; validation.md lists everything
    private const int MaxSummaryProblems = 500;
    // ScriptDom can report a cascade of errors after the first; a few per batch are enough to find it
    private const int MaxErrorsListedPerBatch = 3;

    /// <summary>Validates every .sql file in <see cref="ScriptWriter.Files"/> and registers <c>validation.md</c> with the review log.</summary>
    public async Task<ValidationSummary> ValidateAsync(ScriptContext ctx)
    {
        // statistics and schema-bound references for the 04 rule (cached: 04's generator already read them)
        var extra = await ctx.Probes.ColumnDependentsAsync(ctx.Ct);
        return await Task.Run(() => Validate(ctx, extra), ctx.Ct);
    }

    private static ValidationSummary Validate(ScriptContext ctx, IReadOnlyList<CatalogColumnDependent>? extra)
    {
        var files = ctx.Writer.Files
            .Where(IsSqlFile)
            .OrderBy(f => f.Folder, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        // Files are independent (SET state only carries within a file), so they parse in parallel.
        // Biggest first with load balancing: the _ALL files are a large share of the work and
        // should not start last on one thread.
        var reports = new FileReport[files.Count];
        var bodyClaims = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var order = Enumerable.Range(0, files.Count).OrderByDescending(i => SizeOf(files[i].FullPath)).ToArray();
        Parallel.ForEach<int>(
            Partitioner.Create(order, loadBalance: true),
            new ParallelOptions { CancellationToken = ctx.Ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            i => reports[i] = CheckFileGuarded(ctx, files[i], bodyClaims));
        ctx.Ct.ThrowIfCancellationRequested();

        var (altersChecked, alterFindings) = CheckColumnTypes(ctx, extra);
        var (renamesChecked, renameFindings) = CheckRenames(ctx);

        var findings = reports.SelectMany(r => r.Findings).Concat(alterFindings).Concat(renameFindings).ToList();
        var summary = new ValidationSummary
        {
            Batches = reports.Sum(r => r.Batches),
            SyntaxErrors = reports.Sum(r => r.SyntaxErrors),
            ModulesChecked = reports.Sum(r => r.ModulesChecked),
            ModulesExact = reports.Sum(r => r.ModulesExact),
        };
        summary.Problems.AddRange(findings.Take(MaxSummaryProblems).Select(f => $"{Heading(f.Kind)}: {f.Text}".ReplaceLineEndings(" ")));
        if (findings.Count > MaxSummaryProblems)
            summary.Problems.Add($"... and {findings.Count - MaxSummaryProblems} more - see {ReviewLog.Folder}\\{ReportFile}.");

        ctx.Review.SetDocument(ReportFile, Report(ctx, files, reports, summary, findings, altersChecked, renamesChecked));
        return summary;
    }

    // ------------------------------------------------------------------ one file

    // A validator bug on one file must not fail the whole run (the scripts are already on disk): it becomes a problem.
    private static FileReport CheckFileGuarded(ScriptContext ctx, WrittenFile file, ConcurrentDictionary<string, byte> bodyClaims)
    {
        try
        {
            return CheckFile(ctx, file, bodyClaims);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var r = new FileReport();
            r.Add(Check.File, $"{Display(file)}: could not be validated ({e.GetType().Name}: {e.Message})");
            return r;
        }
    }

    private static FileReport CheckFile(ScriptContext ctx, WrittenFile file, ConcurrentDictionary<string, byte> bodyClaims)
    {
        var r = new FileReport();
        var path = Display(file);
        string text;
        try
        {
            text = ScriptWriter.ReadBack(file.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            r.Add(Check.File, $"{path}: cannot be read back ({e.Message})");
            return r;
        }

        // Parse every batch under the QUOTED_IDENTIFIER in force at that point: the module's own
        // setting for a module file, otherwise ON, then whatever SET batches in the file switch it to
        // (03's _ALL turns it OFF around a QUOTED_IDENTIFIER OFF module). states[i] is the SET state a
        // batch runs under, which is also what a module created by that batch is stored with.
        var batches = ScriptText.SplitBatches(text);
        var states = new SetState[batches.Count];
        var state = new SetState(null, null);
        var parseQi = file.ExpectedSettings?.QuotedIdentifier ?? true;
        for (var i = 0; i < batches.Count; i++)
        {
            // Parallel.ForEach reports the cancellation itself; throwing here would surface as an AggregateException
            if (ctx.Ct.IsCancellationRequested) return r;
            var b = batches[i];
            states[i] = state;
            var errors = Parse(b.Text, parseQi, out var fragment);
            r.Batches++;
            if (errors.Count > 0)
            {
                r.SyntaxErrors += errors.Count;
                foreach (var e in errors.Take(MaxErrorsListedPerBatch))
                    r.Add(Check.Syntax, $"{path}:{b.StartLine + e.Line - 1}:{e.Column}: Msg {e.Number}: {e.Message}");
                if (errors.Count > MaxErrorsListedPerBatch)
                    r.Add(Check.Syntax, $"{path}:{b.StartLine}: {errors.Count - MaxErrorsListedPerBatch} more error(s) in this batch");
            }
            state = ApplySets(state, fragment, errors.Count == 0, b.Text);
            if (state.QuotedId is bool q) parseQi = q;
        }

        switch (file.Kind)
        {
            case WrittenFileKind.RawModule:
                CheckRawModule(ctx, file, path, batches, states, r);
                break;
            case WrittenFileKind.ExecBlockModules:
                CheckExecModules(ctx, file, path, text, batches, states, r, bodyClaims);
                break;
            default:
                // an exec block in a plain DDL file was never recorded as a module, so nothing would prove its text
                foreach (var b in batches.Where(b => b.Text.Contains(ExecMarker, StringComparison.Ordinal)))
                    r.Add(Check.Module, $"{path}:{b.StartLine}: holds an exec block, but the file is not recorded as a module file, so its text was not checked");
                break;
        }
        return r;
    }

    /// <summary>03/07 objects and 02 recreates: MODHDR + the raw definition. The last batch must be the definition, byte for byte.</summary>
    private static void CheckRawModule(ScriptContext ctx, WrittenFile file, string path, List<SqlBatch> batches, SetState[] states, FileReport r)
    {
        r.ModulesChecked++;
        var useTarget = UsesTargetText(file);
        var side = SideLabel(ctx, useTarget);
        var name = file.Object?.ToString() ?? path;
        var ok = true;

        ModuleRow? catalog = null;
        if (file.Object is not { } obj)
        {
            r.Add(Check.Module, $"{path}: no object is recorded for this module file, so its text cannot be compared with the catalog");
            ok = false;
        }
        else if ((catalog = FindModule(ctx, obj, useTarget)) is null)
        {
            r.Add(Check.Module, $"{path}: {name} is not in the {side} catalog, so its text cannot be confirmed");
            ok = false;
        }
        else if (catalog.Definition is null)
        {
            r.Add(Check.Module, $"{path}: {name} has no definition in the {side} catalog (encrypted or CLR), yet a file was written for it");
            ok = false;
        }
        else if (file.ExpectedModuleText is { } recorded && !string.Equals(recorded, catalog.Definition, StringComparison.Ordinal))
        {
            r.Add(Check.Module, $"{path}: the text recorded for {name} is not its {side} definition: {Diff(catalog.Definition, recorded)}");
            ok = false;
        }

        var want = catalog?.Definition ?? file.ExpectedModuleText;
        if (want is null)
        {
            r.Add(Check.Module, $"{path}: no expected definition is recorded for {name}");
            return;
        }
        if (batches.Count == 0)
        {
            r.Add(Check.Module, $"{path}: the file is empty, expected {name}'s definition");
            return;
        }

        var last = batches[^1];
        if (!string.Equals(last.Text, want, StringComparison.Ordinal))
        {
            var why = ScriptText.HasBatchBreakingLine(want)
                ? " - the definition holds a line SSMS reads as GO or a sqlcmd command, so splitting the file cuts it (an SSMS-only risk: sqlcmd does not split inside strings and comments); write this module through ExecBlock instead"
                : "";
            r.Add(Check.Module, $"{path}:{last.StartLine}: the last batch is not {name}'s {side} definition byte for byte: {Diff(want, last.Text)}{why}");
            ok = false;
        }
        if (ok) r.ModulesExact++;

        var need = catalog is not null ? ModuleSettings.Of(catalog) : file.ExpectedSettings;
        CheckSettings(r, $"{path}:{last.StartLine}", name, side, states[^1], need);
    }

    /// <summary>
    /// _ALL files and 08 objects/rollback: every exec block, in order, must rebuild its recorded module
    /// both as written and after SSMS turns every line ending into CRLF; the rebuilt body is parsed too.
    /// </summary>
    private static void CheckExecModules(
        ScriptContext ctx, WrittenFile file, string path, string text, List<SqlBatch> batches, SetState[] states,
        FileReport r, ConcurrentDictionary<string, byte> bodyClaims)
    {
        var execAt = new List<int>();
        for (var i = 0; i < batches.Count; i++)
            if (batches[i].Text.Contains(ExecMarker, StringComparison.Ordinal)) execAt.Add(i);
        var ssms = ScriptText.SplitBatches(ScriptText.SsmsRewrite(text))
            .Where(b => b.Text.Contains(ExecMarker, StringComparison.Ordinal)).ToList();
        var expected = file.ExpectedModules;

        if (execAt.Count != expected.Count)
            r.Add(Check.Module, $"{path}: holds {execAt.Count} exec block(s), but {expected.Count} module(s) were recorded for it");
        if (ssms.Count != execAt.Count)
            r.Add(Check.Module, $"{path}: after an SSMS line-ending rewrite the file splits into {ssms.Count} exec block(s) instead of {execAt.Count}");

        var useTarget = UsesTargetText(file);
        var side = SideLabel(ctx, useTarget);
        for (var k = 0; k < expected.Count; k++)
        {
            if (ctx.Ct.IsCancellationRequested) return;
            r.ModulesChecked++;
            var exp = expected[k];
            var name = exp.Object.ToString();
            var ok = true;

            var catalog = FindModule(ctx, exp.Object, useTarget);
            if (catalog is null)
            {
                r.Add(Check.Module, $"{path}: {name} is not in the {side} catalog, so its text cannot be confirmed");
                ok = false;
            }
            else if (catalog.Definition is null)
            {
                r.Add(Check.Module, $"{path}: {name} has no definition in the {side} catalog (encrypted or CLR), yet it was scripted");
                ok = false;
            }
            else if (!string.Equals(exp.Text, catalog.Definition, StringComparison.Ordinal))
            {
                r.Add(Check.Module, $"{path}: the text recorded for {name} is not its {side} definition: {Diff(catalog.Definition, exp.Text)}");
                ok = false;
            }
            var want = catalog?.Definition ?? exp.Text;

            if (k >= execAt.Count)
            {
                r.Add(Check.Module, $"{path}: no exec block for module {k + 1} ({name})");
                continue;
            }

            var batchIndex = execAt[k];
            var batch = batches[batchIndex];
            string? built = null;
            if (!ScriptText.TryEvaluateExecBlock(batch.Text, out built, out var err))
            {
                r.Add(Check.Module, $"{path}:{batch.StartLine}: exec block {k + 1} ({name}) cannot be evaluated: {err}");
                ok = false;
            }
            else if (!string.Equals(built, want, StringComparison.Ordinal))
            {
                r.Add(Check.Module, $"{path}:{batch.StartLine}: exec block {k + 1} does not rebuild {name}'s {side} definition: {Diff(want, built ?? "")}");
                ok = false;
            }

            if (k < ssms.Count)
            {
                if (!ScriptText.TryEvaluateExecBlock(ssms[k].Text, out var afterSsms, out var err2))
                {
                    r.Add(Check.Module, $"{path}:{batch.StartLine}: after an SSMS line-ending rewrite, exec block {k + 1} ({name}) cannot be evaluated: {err2}");
                    ok = false;
                }
                else if (!string.Equals(afterSsms, want, StringComparison.Ordinal))
                {
                    r.Add(Check.Module, $"{path}:{batch.StartLine}: after an SSMS line-ending rewrite, exec block {k + 1} does not rebuild {name}'s {side} definition: {Diff(want, afterSsms ?? "")}");
                    ok = false;
                }
            }
            else ok = false; // the count mismatch above already says why

            if (ok) r.ModulesExact++;

            var need = catalog is not null ? ModuleSettings.Of(catalog) : exp.Settings;
            CheckSettings(r, $"{path}:{batch.StartLine}", name, side, states[batchIndex], need);
            if (built is not null)
                ParseBody(r, $"{path}:{batch.StartLine}", name, built, need.QuotedIdentifier, bodyClaims);
        }
    }

    /// <summary>
    /// Parses a module body rebuilt from an exec block. The same body often appears twice (objects file and
    /// _ALL); it is parsed and its errors counted once.
    /// </summary>
    private static void ParseBody(FileReport r, string where, string name, string body, bool quotedId, ConcurrentDictionary<string, byte> claims)
    {
        if (!claims.TryAdd((quotedId ? "1" : "0") + body, 0)) return;
        var errors = Parse(body, quotedId, out _);
        if (errors.Count == 0) return;
        r.SyntaxErrors += errors.Count;
        foreach (var e in errors.Take(MaxErrorsListedPerBatch))
            r.Add(Check.Syntax, $"{where}: {name} (rebuilt from its exec block), definition line {e.Line}:{e.Column}: Msg {e.Number}: {e.Message}");
        if (errors.Count > MaxErrorsListedPerBatch)
            r.Add(Check.Syntax, $"{where}: {name}: {errors.Count - MaxErrorsListedPerBatch} more error(s) in the definition");
    }

    /// <summary>
    /// A module is stored with the ANSI_NULLS and QUOTED_IDENTIFIER of the session that ran its CREATE, so
    /// the SET state at that batch must match the module's own settings. A file that never sets one leaves
    /// it to the client (sqlcmd and SSMS differ), which is reported too.
    /// </summary>
    private static void CheckSettings(FileReport r, string where, string name, string side, SetState at, ModuleSettings? need)
    {
        if (need is null) return;
        Compare("QUOTED_IDENTIFIER", at.QuotedId, need.QuotedIdentifier);
        Compare("ANSI_NULLS", at.AnsiNulls, need.AnsiNulls);

        void Compare(string option, bool? have, bool want)
        {
            if (have is null)
                r.Add(Check.Settings, $"{where}: {name} - the file does not set {option} before this module, so it would get the client's default; the {side} module has {option} {OnOff(want)}");
            else if (have != want)
                r.Add(Check.Settings, $"{where}: {name} would be created with {option} {OnOff(have.Value)}, the {side} module has {option} {OnOff(want)}");
        }
    }

    // ------------------------------------------------------------------ 04 safety

    /// <summary>
    /// check_scripts.py §5 on the files as written: every ALTER COLUMN must widen or relax only, against the
    /// target as it is now. Also the Msg 5074/8111 rules (a type change on a column an index, key, constraint
    /// or computed column depends on fails at run time) and the collation the ALTER would leave behind.
    /// The 04 plan (<see cref="CoreFacts.IntendedAlters"/>) must match the files it claims to have written.
    /// </summary>
    private static (int Checked, List<Finding> Findings) CheckColumnTypes(ScriptContext ctx, IReadOnlyList<CatalogColumnDependent>? extra)
    {
        var findings = new List<Finding>();
        var files = ctx.Writer.FilesIn(ColumnTypesFolder).Where(f => IsSqlFile(f) && f.Kind == WrittenFileKind.Statements).ToList();
        var objectFiles = files.Where(f => f.IsObjectFile).ToList();
        var scan = objectFiles.Count > 0 ? objectFiles : files; // _ALL repeats the objects files
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stmts = new List<AlterStmt>();
        foreach (var f in scan)
        {
            string text;
            try { text = ScriptWriter.ReadBack(f.FullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; } // CheckFile reported it
            texts[f.RelativePath] = text;
            foreach (var b in ScriptText.SplitBatches(text))
            foreach (Match m in AlterRx().Matches(b.Text))
            {
                var line = b.StartLine + CountLf(b.Text, m.Index);
                stmts.Add(new AlterStmt($"{Display(f)}:{line}", Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value),
                    Unescape(m.Groups[3].Value), m.Groups[4].Value.Trim(),
                    !m.Groups[5].Value.StartsWith("NOT", StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (var ia in ctx.Facts.IntendedAlters)
            if (texts.TryGetValue("objects\\" + ia.File, out var t) && !t.Contains(ia.Sql, StringComparison.Ordinal))
                findings.Add(new Finding(Check.ColumnTypes, $"{ColumnTypesFolder}\\objects\\{ia.File}: the 04 plan says it holds \"{ia.Sql}\", but the file does not"));

        ColumnCover? cover = null;
        foreach (var s in stmts)
        {
            cover ??= ColumnCover.Build(ctx.Target, extra);
            CheckAlter(ctx, s, cover, findings);
        }
        return (stmts.Count, findings);
    }

    private static void CheckAlter(ScriptContext ctx, AlterStmt s, ColumnCover cover, List<Finding> findings)
    {
        void Bad(string why) => findings.Add(new Finding(Check.ColumnTypes, $"{s.Where}: [{s.Sch}].[{s.Tbl}].[{s.Col}] {why}"));

        var table = new ObjectName(s.Sch, s.Tbl);
        var p = ctx.Target.FindColumn(table, s.Col);
        if (p is null) { Bad($"does not exist in {ctx.TargetEndpoint.Label}"); return; }
        if (p.IsComputed) { Bad($"is a computed column in {ctx.TargetEndpoint.Label}; ALTER COLUMN cannot change it"); return; }
        if (!TryParseType(s.Type, out var nt)) { Bad($"has a type the check cannot read: {s.Type}"); return; }

        var deps = cover.Of(table, p.Col);

        if (!s.Nullable && p.IsNullable)
            Bad($"tightens NULL to NOT NULL ({ctx.TargetEndpoint.Label} allows NULL)");
        if (s.Nullable && !p.IsNullable && deps.FirstOrDefault(d => d.Kind == ColumnDependentKind.PrimaryKey) is { } pk)
            Bad($"would make a column of {pk.Name} nullable (Msg 8111); a primary-key column cannot be NULL");

        var pd = p.Dt.ToLowerInvariant();
        var nd = nt.Dt.ToLowerInvariant();
        var bareTarget = Ddl.QType(p.Dt, p.MaxLength, p.Precision, p.Scale, null, null);
        if (!string.Equals(nt.Bare, bareTarget, StringComparison.OrdinalIgnoreCase))
        {
            if (IntSize(nd) is int ns && IntSize(pd) is int ps)
            {
                if (ns < ps) Bad($"NARROWS {bareTarget} to {nt.Bare}");
            }
            else if (IsString(nd) && IsString(pd))
            {
                var wn = nt.Length is null ? (int?)null : nt.Length == "max" ? int.MaxValue : int.TryParse(nt.Length, out var len) ? (int?)len : null;
                var wp = p.MaxLength == -1 ? int.MaxValue : pd[0] == 'n' ? p.MaxLength / 2 : p.MaxLength;
                if (wn is int w && w < wp) Bad($"NARROWS {bareTarget} to {nt.Bare}");
                if (pd[0] == 'n' && nd[0] != 'n') Bad($"turns Unicode {bareTarget} into non-Unicode {nt.Bare}, which can lose characters");
            }
            else Bad($"changes the type outside the widen-only rules ({bareTarget} to {nt.Bare})");

            // ALTER TABLE ... ALTER COLUMN rules (Msg 5074): the same rule 04 holds changes back with
            var blockers = ColumnCover.Blockers(deps, typeChanges: true, pd, nd, nt.Length == "max");
            if (blockers.Count > 0)
                Bad($"changes the type of a column that {string.Join(", ", blockers)} depend(s) on; SQL Server refuses it (Msg 5074)");
        }
        else if (s.Nullable != p.IsNullable && ColumnCover.Blockers(deps, typeChanges: false, pd, nd, false) is { Count: > 0 } held)
            Bad($"changes NULL / NOT NULL on a column that {string.Join(", ", held)} depend(s) on; SQL Server refuses it (Msg 5074)");

        if (IsString(nd))
        {
            var expectColl = !string.IsNullOrEmpty(p.CollationName) && p.CollationName != ctx.Target.Collation ? p.CollationName : null;
            if (!string.Equals(nt.Collate, expectColl, StringComparison.OrdinalIgnoreCase))
                Bad($"changes the column's collation from {p.CollationName ?? ctx.Target.Collation} to {nt.Collate ?? ctx.Target.Collation + " (the database default)"}");
        }
    }

    // ------------------------------------------------------------------ 09 renames

    /// <summary>
    /// check_formatting.py §3 against the un-aligned target catalog: every old name must exist with exactly
    /// that spelling, and every rename must change letter case only.
    /// </summary>
    private static (int Checked, List<Finding> Findings) CheckRenames(ScriptContext ctx)
    {
        var findings = new List<Finding>();
        var raw = ctx.RawTarget;
        var label = ctx.TargetEndpoint.Label;
        foreach (var rn in ctx.Facts.Renames)
        {
            var where = $"{RenamesFolder}\\objects\\{rn.File}";
            if (string.Equals(rn.Old, rn.New, StringComparison.Ordinal) || !string.Equals(rn.Old, rn.New, StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding(Check.Renames, $"{where}: {rn.Kind} '{rn.Old}' to '{rn.New}' is not a letter-case-only rename"));

            var table = new ObjectName(rn.Schema, rn.Table);
            bool OnTable(ITableRow x) => string.Equals(x.Sch, rn.Schema, StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(x.Tbl, rn.Table, StringComparison.OrdinalIgnoreCase);
            bool? found = rn.Kind switch
            {
                "table" => raw.Tables.Any(t => string.Equals(t.Name, rn.Old, StringComparison.Ordinal) &&
                                               string.Equals(t.Schema, rn.Schema, StringComparison.OrdinalIgnoreCase)),
                "column" => raw.ColumnsOf(table).Any(c => string.Equals(c.Col, rn.Old, StringComparison.Ordinal)),
                "index" => raw.Indexes.Any(i => OnTable(i) && string.Equals(i.IName, rn.Old, StringComparison.Ordinal)),
                "object" => raw.Keys.Cast<INamedConstraint>().Concat(raw.FKeys).Concat(raw.Checks).Concat(raw.Defaults)
                    .Any(c => string.Equals(c.CName, rn.Old, StringComparison.Ordinal)),
                _ => null,
            };
            if (found is null)
                findings.Add(new Finding(Check.Renames, $"{where}: unknown rename kind '{rn.Kind}'"));
            else if (found == false)
                findings.Add(new Finding(Check.Renames, $"{where}: {rn.Kind} '{rn.Old}' ({rn.Schema}.{rn.Table}) does not exist in {label} with that exact spelling"));
        }
        return (ctx.Facts.Renames.Count, findings);
    }

    // ------------------------------------------------------------------ report

    private static string Report(
        ScriptContext ctx, List<WrittenFile> files, FileReport[] reports, ValidationSummary s, List<Finding> findings,
        int altersChecked, int renamesChecked)
    {
        var sb = new StringBuilder();
        sb.Append("# Validation\n\n");
        sb.Append(s.Passed ? "**Passed.** " : "**Problems found.** Read the list below before running anything. ");
        sb.Append("Every generated .sql file was checked offline on this machine; nothing was sent to any server.\n\n");

        sb.Append("| Check | Result |\n|---|---:|\n");
        sb.Append($"| .sql files | {files.Count} |\n");
        sb.Append($"| Batches parsed | {s.Batches} |\n");
        sb.Append($"| Syntax errors | {s.SyntaxErrors} |\n");
        sb.Append($"| Module texts checked | {s.ModulesChecked} |\n");
        sb.Append($"| Module texts that rebuild byte for byte | {s.ModulesExact} |\n");
        sb.Append($"| 04 ALTER COLUMN statements checked | {altersChecked} |\n");
        sb.Append($"| 09 renames checked | {renamesChecked} |\n");
        sb.Append($"| Problems | {findings.Count} |\n\n");

        if (files.Count > 0)
        {
            sb.Append("## Per folder\n\n");
            sb.Append("| Folder | Files | Batches | Syntax errors | Modules checked | Byte-exact |\n|---|---:|---:|---:|---:|---:|\n");
            foreach (var g in files.Select((f, i) => (f.Folder, R: reports[i])).GroupBy(x => x.Folder).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.Append($"| {(g.Key.Length == 0 ? "(root)" : g.Key)} | {g.Count()} | {g.Sum(x => x.R.Batches)} | {g.Sum(x => x.R.SyntaxErrors)} | {g.Sum(x => x.R.ModulesChecked)} | {g.Sum(x => x.R.ModulesExact)} |\n");
            sb.Append('\n');
        }

        sb.Append("## Problems\n\n");
        if (findings.Count == 0) sb.Append("No problems.\n\n");
        foreach (var g in findings.GroupBy(f => f.Kind).OrderBy(g => g.Key))
        {
            sb.Append($"### {Heading(g.Key)} ({g.Count()})\n\n");
            foreach (var f in g) sb.Append("- ").Append(f.Text.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }

        sb.Append("## What was checked\n\n");
        sb.Append("- **Syntax.** Every file is split on its GO lines (the SSMS rule) and every batch is parsed with ScriptDom, ");
        sb.Append("SQL Server 2022 grammar, under the QUOTED_IDENTIFIER the batch would run with. Module bodies rebuilt from exec blocks are parsed too.\n");
        sb.Append($"- **Module text.** The text each module would be stored with is compared, byte for byte, with the definition in the catalog: ");
        sb.Append($"{ctx.SourceEndpoint.Label}'s for forward scripts, {ctx.TargetEndpoint.Label}'s for `rollback\\` files and the 02 recreates. ");
        sb.Append("Per-object module files must end with the exact definition. Exec blocks (`_ALL.sql`, 08) are evaluated offline as SQL Server would build `@sql`, ");
        sb.Append("both as written and after an editor turns every line ending into CRLF, as SSMS does when it opens a file.\n");
        sb.Append("- **SET options.** A module is stored with the ANSI_NULLS and QUOTED_IDENTIFIER of the session that creates it; the file must set both to the module's own values first.\n");
        sb.Append($"- **04 column types.** Every ALTER COLUMN must only widen or relax against {ctx.TargetEndpoint.Label} as it is now: no narrowing, no NOT NULL tightening, ");
        sb.Append("no Unicode to non-Unicode, no collation change, and no type change under an index, key, constraint or computed column (SQL Server refuses those, Msg 5074).\n");
        sb.Append($"- **09 renames.** Every old name exists in {ctx.TargetEndpoint.Label} with exactly that spelling, and only its letter case changes.\n");
        return sb.ToString();
    }

    private static string Heading(Check c) => c switch
    {
        Check.File => "Files",
        Check.Syntax => "Syntax errors",
        Check.Module => "Module text",
        Check.Settings => "Module SET options",
        Check.ColumnTypes => "04 column types",
        Check.Renames => "09 renames",
        _ => c.ToString(),
    };

    // ------------------------------------------------------------------ helpers

    private static IList<Dom.ParseError> Parse(string sql, bool quotedIdentifiers, out Dom.TSqlFragment? fragment)
    {
        var parser = new Dom.TSql160Parser(quotedIdentifiers);
        using var reader = new StringReader(sql);
        fragment = parser.Parse(reader, out var errors);
        return errors;
    }

    /// <summary>
    /// The SET state after a batch: its top-level SET ANSI_NULLS / QUOTED_IDENTIFIER / ANSI_DEFAULTS statements
    /// (a SET inside a module body is part of that module, not the session). When the batch did not parse,
    /// only a batch made of SET lines alone is trusted.
    /// </summary>
    private static SetState ApplySets(SetState s, Dom.TSqlFragment? fragment, bool parsed, string batch)
    {
        if (parsed && fragment is Dom.TSqlScript script)
        {
            foreach (var b in script.Batches)
            foreach (var st in b.Statements)
                if (st is Dom.PredicateSetStatement ps) s = s.Apply(ps.Options, ps.IsOn);
            return s;
        }
        if (!SetOnlyBatchRx().IsMatch(batch)) return s;
        foreach (Match m in SetLineRx().Matches(batch))
        {
            var on = m.Groups[2].Value.Equals("ON", StringComparison.OrdinalIgnoreCase);
            s = m.Groups[1].Value.ToUpperInvariant() switch
            {
                "QUOTED_IDENTIFIER" => s.Apply(Dom.SetOptions.QuotedIdentifier, on),
                "ANSI_NULLS" => s.Apply(Dom.SetOptions.AnsiNulls, on),
                _ => s.Apply(Dom.SetOptions.AnsiDefaults, on),
            };
        }
        return s;
    }

    /// <summary>Rollbacks hold the target's current text; 02 drops and recreates target modules from the target's own text.</summary>
    private static bool UsesTargetText(WrittenFile f) =>
        f.IsRollback || f.Folder.StartsWith("02", StringComparison.Ordinal);

    private static string SideLabel(ScriptContext ctx, bool target) =>
        target ? ctx.TargetEndpoint.Label : ctx.SourceEndpoint.Label;

    // Target is case-aligned to the source spelling the generators use; RawTarget catches a file keyed by the target's own spelling.
    private static ModuleRow? FindModule(ScriptContext ctx, ObjectName name, bool target) =>
        target ? ctx.Target.FindModule(name) ?? ctx.RawTarget.FindModule(name) : ctx.Source.FindModule(name);

    private static bool IsSqlFile(WrittenFile f) =>
        f.Kind is not (WrittenFileKind.Runner or WrittenFileKind.Markdown) &&
        f.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static string Display(WrittenFile f) => f.Folder.Length == 0 ? f.RelativePath : f.Folder + "\\" + f.RelativePath;

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static int CountLf(string s, int end)
    {
        var n = 0;
        for (var i = 0; i < end && i < s.Length; i++) if (s[i] == '\n') n++;
        return n;
    }

    private static string Unescape(string bracketed) => bracketed.Replace("]]", "]");

    private static int? IntSize(string dt) => dt switch { "tinyint" => 1, "smallint" => 2, "int" => 4, "bigint" => 8, _ => null };

    private static bool IsString(string dt) => dt is "char" or "varchar" or "nchar" or "nvarchar";

    private static bool TryParseType(string text, out ParsedType type)
    {
        var m = TypeRx().Match(text);
        if (!m.Success) { type = default; return false; }
        var collateAt = text.IndexOf(" COLLATE ", StringComparison.OrdinalIgnoreCase);
        type = new ParsedType(
            Unescape(m.Groups[1].Value),
            m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : null,
            m.Groups[4].Success ? m.Groups[4].Value : null,
            (collateAt < 0 ? text : text[..collateAt]).Trim());
        return true;
    }

    /// <summary>Where two texts first differ, with the characters around it made visible.</summary>
    private static string Diff(string want, string got)
    {
        var n = Math.Min(want.Length, got.Length);
        var i = 0;
        while (i < n && want[i] == got[i]) i++;
        var line = 1 + CountLf(want, i);
        return $"{want.Length} vs {got.Length} characters, first difference at character {i + 1} (line {line}): expected \"{Snip(want, i)}\", found \"{Snip(got, i)}\"";

        static string Snip(string s, int at)
        {
            if (at >= s.Length) return "(end of text)";
            var sb = new StringBuilder();
            foreach (var c in s.AsSpan(at, Math.Min(30, s.Length - at)))
                sb.Append(c switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    '"' => "\\\"",
                    _ when char.IsControl(c) || char.IsSurrogate(c) => $"\\u{(int)c:X4}",
                    _ => c.ToString(),
                });
            return sb.ToString();
        }
    }

    // generate.py 04 statement shape (Ddl.AlterColumn); names may hold ']]'
    [GeneratedRegex(@"ALTER TABLE \[((?:[^\]]|\]\])+)\]\.\[((?:[^\]]|\]\])+)\] ALTER COLUMN \[((?:[^\]]|\]\])+)\] (.+?) (NULL|NOT NULL)[ \t]*;?[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AlterRx();

    // Ddl.QType output: [dt], [dt](n|max), [dt](p, s), then an optional COLLATE x
    [GeneratedRegex(@"^\[((?:[^\]]|\]\])+)\](?:\(\s*(max|\d+)\s*(?:,\s*(\d+)\s*)?\))?(?:\s+COLLATE\s+(\S+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeRx();

    [GeneratedRegex(@"^\s*(?:SET\s+[A-Z_]+(?:\s*,\s*[A-Z_]+)*\s+(?:ON|OFF)\s*;?\s*)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetOnlyBatchRx();

    [GeneratedRegex(@"\bSET\s+(QUOTED_IDENTIFIER|ANSI_NULLS|ANSI_DEFAULTS)\s+(ON|OFF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetLineRx();

    // ------------------------------------------------------------------ types

    private enum Check { File, Syntax, Module, Settings, ColumnTypes, Renames }

    private sealed record Finding(Check Kind, string Text);

    private sealed class FileReport
    {
        public int Batches;
        public int SyntaxErrors;
        public int ModulesChecked;
        public int ModulesExact;
        public List<Finding> Findings { get; } = [];
        public void Add(Check kind, string text) => Findings.Add(new Finding(kind, text));
    }

    /// <summary>ANSI_NULLS / QUOTED_IDENTIFIER as set so far in a file; null = never set (the client's default applies).</summary>
    private readonly record struct SetState(bool? QuotedId, bool? AnsiNulls)
    {
        public SetState Apply(Dom.SetOptions options, bool on)
        {
            var s = this;
            // SET ANSI_DEFAULTS switches both, among others
            if ((options & (Dom.SetOptions.QuotedIdentifier | Dom.SetOptions.AnsiDefaults)) != 0) s = s with { QuotedId = on };
            if ((options & (Dom.SetOptions.AnsiNulls | Dom.SetOptions.AnsiDefaults)) != 0) s = s with { AnsiNulls = on };
            return s;
        }
    }

    private sealed record AlterStmt(string Where, string Sch, string Tbl, string Col, string Type, bool Nullable);

    /// <param name="Length">The (n) or (max) of a string type, or the precision of decimal; null when absent.</param>
    /// <param name="Collate">The COLLATE name, or null.</param>
    /// <param name="Bare">The type without COLLATE, as Ddl.QType writes it.</param>
    private readonly record struct ParsedType(string Dt, string? Length, string? Collate, string Bare);

}
