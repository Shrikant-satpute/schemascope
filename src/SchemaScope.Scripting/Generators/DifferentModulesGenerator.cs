using System.Globalization;
using System.Text;

namespace SchemaScope.Scripting;

/// <summary>
/// Folder 10_different_modules: replaces procedures, views, functions and triggers whose code really differs
/// (SchemaScope's compare status Different) with the source's text, with 08's transactional DROP + CREATE and a
/// rollback of the target's current text. Off by default and never in RUN.ps1: the target's version may hold a fix
/// made only there, and a new parameter list can break the application's calls.
/// </summary>
/// <remarks>
/// Held back (99_review\different_not_applied.md), because the replacement would fail or lose something:
/// no text on one side; quarantined or excluded; does not compile on the source; names a column or reads an object
/// the target will not have after 01 and 03; takes a table type whose columns differ while 02 is not written; a
/// trigger disabled on the source; and every DROP guard of 08 (permissions, extended properties, schema-bound
/// users, signatures, indexed views, disabled or ordered triggers, forced plans), or a guard that could not be read.
/// Replaced, with what to check in 99_review\different_modules.md: a changed parameter list, and a module the target
/// changed more recently than the source did.
/// </remarks>
public sealed class DifferentModulesGenerator : IFolderGenerator
{
    public const string Folder = "10_different_modules";

    public async Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx)
    {
        var ct = ctx.Ct;
        var src = ctx.Source;
        var srcLabel = ctx.SourceEndpoint.Label;
        var tgtLabel = ctx.TargetEndpoint.Label;
        var result = new FolderResult { Folder = Folder, Title = "Modules with different code", InRunAll = false };

        ctx.Review.Describe(ReviewLog.DifferentNotApplied, "Modules with different code that are not replaced",
            $"The code of each one differs between {srcLabel} and {tgtLabel}, but replacing it with the {srcLabel} text would fail " +
            $"or lose something {tgtLabel} has, so 10_different_modules leaves it as it is. Change it by hand if you are sure.");

        void Hold(ObjectName k, ModuleRow m, string why) =>
            ctx.Review.Hold(ReviewLog.DifferentNotApplied, FormattingGenerator.KindWord(m.OType), k.ToString(), null, why, Folder);

        // ---- candidates: Different in the compare, on both sides, really different, scriptable
        var candidates = new List<ModuleRow>();
        foreach (var k in src.SortedModules())
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.DifferentCandidates.ContainsKey(k.ToString()) || !ctx.InScope(k)) continue;
            var m = src.Modules[k];
            if (ctx.Target.FindModule(k) is not { } t) continue;
            if (m.Definition is null || t.Definition is null)
            {
                Hold(k, m, "no text on one side (encrypted or CLR) - it cannot be copied");
                continue;
            }
            if (string.Equals(m.Definition, t.Definition, StringComparison.Ordinal) ||
                string.Equals(TsqlTokens.Light(m.Definition), TsqlTokens.Light(t.Definition), StringComparison.Ordinal))
                continue;
            if (ctx.Facts.QuarantineNames.Contains(k.Name.ToLowerInvariant()))
            {
                Hold(k, m, "quarantined: a .sql file with this name is under the quarantine folder");
                continue;
            }
            if (ctx.IsExcluded(k))
            {
                Hold(k, m, "matches an exclude pattern");
                continue;
            }
            if (Ddl.DropWord(m.OType) is null || Ddl.DropWord(t.OType) is null)
            {
                Hold(k, m, $"object type {m.OType.Trim()} cannot be replaced by DROP + CREATE");
                continue;
            }
            candidates.Add(m);
        }

        // ---- the source text must compile where it came from
        var checks = new CompileCheck[candidates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, c) => { checks[i] = await ctx.SourceProbes.CompileCheckAsync(candidates[i], c); });
        var notes = new Dictionary<ObjectName, List<string>>(ObjectName.IgnoreCase);
        void AddNote(ObjectName k, string note)
        {
            if (!notes.TryGetValue(k, out var l)) notes[k] = l = [];
            l.Add(note);
        }
        var compiling = new List<ModuleRow>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var m = candidates[i];
            if (checks[i].Compiles == false)
            {
                Hold(m.Name, m, $"does not compile in {srcLabel} (Msg {checks[i].ErrorNumber}: {checks[i].Message}) - its CREATE fails anywhere");
                continue;
            }
            if (checks[i].Note is { } note) AddNote(m.Name, note);
            compiling.Add(m);
        }

        // ---- what the target will have after 01 and 03
        var unavailable = ctx.Facts.UnavailableColumns.Select(u => (u.Table, u.Column)).ToList();
        if (!ctx.Selected(ScriptSection.NewColumns))
            unavailable.AddRange(ctx.Facts.NewColumns.Select(n => (n.Table, n.Column.Col)));
        var byTable = unavailable.GroupBy(u => u.Table, src.NameComparer).ToList();

        var targetObjects = await ctx.Probes.ObjectsAsync(ct);
        var inTarget = new HashSet<ObjectName>(
            targetObjects?.Select(o => o.Name) ?? ctx.Target.Tables.Concat(ctx.Target.Modules.Keys), src.NameComparer);
        var by03 = new HashSet<ObjectName>(
            ctx.Selected(ScriptSection.MissingObjects) ? ctx.Facts.MissingItems.Select(i => i.Object) : [], src.NameComparer);
        var deps = await ctx.SourceProbes.ModuleDependenciesAsync(ct);
        var refs = (deps ?? []).ToLookup(d => d.Referencing, src.NameComparer);
        bool KnownInSource(ObjectName o) => src.Tables.Contains(o) || src.Modules.ContainsKey(o) || src.TableTypes.Contains(o);
        bool Available(ObjectName o) => !KnownInSource(o) || inTarget.Contains(o) || by03.Contains(o);

        var driftedTypes = new HashSet<ObjectName>(ctx.Facts.DriftedTableTypes, src.NameComparer);
        var disabledInSource = new HashSet<ObjectName>(await ctx.SourceProbes.DisabledTriggersAsync(ct) ?? [], ObjectName.IgnoreCase);

        var bindable = new List<ModuleRow>();
        foreach (var m in compiling)
        {
            ct.ThrowIfCancellationRequested();
            var k = m.Name;
            var words = new HashSet<string>(
                TsqlTokens.Tokens(m.Definition!).Where(t => t.Kind == 'I').Select(t => t.Text), StringComparer.OrdinalIgnoreCase);
            var lost = byTable
                .Where(g => words.Contains(g.Key.Name))
                .Select(g => g.FirstOrDefault(u => words.Contains(u.Column)))
                .FirstOrDefault(u => u.Column is not null);
            if (lost.Column is not null)
            {
                Hold(k, m, $"names {lost.Table.Name}.{lost.Column}, a column {tgtLabel} will not have (01_columns does not add it) - " +
                           "SQL Server checks the columns of an existing table when the module is created, so CREATE would fail (Msg 207)");
                continue;
            }
            var missing = refs[k].Select(d => (ObjectName?)d.Referenced)
                .FirstOrDefault(r => !src.NameComparer.Equals(r!.Value, k) && !Available(r.Value));
            if (missing is { } gone)
            {
                var bindsAtCreate = m.OType.Trim() is "V" or "IF" || CoreRun.IsSchemaBound(m.Definition);
                Hold(k, m, bindsAtCreate
                    ? $"reads {gone}, which {tgtLabel} does not have and 03_missing does not create - CREATE would fail (Msg 208)"
                    : $"reads {gone}, which {tgtLabel} does not have and 03_missing does not create - it would fail when it runs (Msg 208)");
                continue;
            }
            var types = src.TtParams.Where(p => src.NameComparer.Equals(p.Module, k) && driftedTypes.Contains(p.Type))
                .Select(p => p.Type.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (types.Count > 0)
            {
                if (!ctx.Facts.TableTypesWritten)
                {
                    Hold(k, m, $"takes table type {string.Join(", ", types)}, whose columns differ in {tgtLabel}, and 02_tabletypes was not written - " +
                               "the source text expects the source's columns");
                    continue;
                }
                AddNote(k, $"takes table type {string.Join(", ", types)} - run 02_tabletypes first");
            }
            if (m.OType.Trim() == "TR" && disabledInSource.Contains(k))
            {
                Hold(k, m, $"disabled in {srcLabel} - CREATE TRIGGER would create it enabled and change what every write to the table does");
                continue;
            }
            bindable.Add(m);
        }

        // ---- 08's DROP guards on the target
        var guards = await ctx.Probes.FormattingGuardsAsync(includeExtraGuards: true, ct);
        var blindGuards = await FormattingGenerator.UncheckedGuardsAsync(ctx);
        var forcedChecked = await ctx.Probes.ForcedPlanNamesAsync(ct) is not null;
        var kept = new List<ModuleRow>();
        foreach (var m in bindable)
        {
            var t = ctx.Target.FindModule(m.Name)!;
            if (guards.TryGetValue(m.Name.Name.ToLowerInvariant(), out var reasons) && reasons.Count > 0)
            {
                Hold(m.Name, m, string.Join("; ", reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
                continue;
            }
            var blind = blindGuards.Where(u => u.AppliesTo(t.OType)).Select(u => u.What).ToList();
            if (blind.Count > 0)
            {
                Hold(m.Name, m, $"could not check on {tgtLabel} whether DROP would lose something ({string.Join(", ", blind)} not readable)");
                continue;
            }
            kept.Add(m);
        }

        // ---- least used first; a view, inline function or schema-bound module after the replaced modules it reads
        var qs = await ctx.Probes.QueryStoreAsync(ct);
        kept = kept
            .OrderBy(m => qs.RunsFor(m.Name.Name))
            .ThenBy(m => m.Obj.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(m => m.Sch.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
        var index = new Dictionary<ObjectName, int>(src.NameComparer);
        for (var i = 0; i < kept.Count; i++) index[kept[i].Name] = i;
        var edges = new List<(int Before, int After)>();
        foreach (var d in deps ?? [])
        {
            if (!index.TryGetValue(d.Referencing, out var a) || !index.TryGetValue(d.Referenced, out var b) || a == b) continue;
            var m = kept[a];
            if (m.OType.Trim() is "V" or "IF" || CoreRun.IsSchemaBound(m.Definition)) edges.Add((b, a));
        }
        kept = CoreRun.StableOrder(kept.Count, edges, []).Select(i => kept[i]).ToList();

        if (kept.Count == 0)
        {
            result.Notes.Insert(0, ScriptGenerator.NothingToDoNote);
            result.Notes.Add(ctx.DifferentCandidates.Count == 0
                ? $"No procedure, view, function or trigger has different code in {tgtLabel}."
                : $"No module qualified - see 99_review\\{ReviewLog.DifferentNotApplied}.");
            ctx.Review.SetDocument(ReviewLog.DifferentModulesFile, Report(ctx, [], qs, null));
            return [result];
        }

        // ---- what each replacement changes for its callers, and whether the target's version is the newer one
        var dates = await ModuleDatesAsync(ctx);
        var rows = new List<Row>();
        var fwd = new StringBuilder();
        var back = new StringBuilder();
        var fwdExpected = new List<ExpectedModule>();
        var backExpected = new List<ExpectedModule>();
        var number = 0;
        foreach (var m in kept)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            var k = m.Name;
            var t = ctx.Target.FindModule(k)!;
            var file = $"{number:000}_{ScriptText.Safe(k.Name)}.sql";
            var srcSettings = ModuleSettings.Of(m);
            var tgtSettings = ModuleSettings.Of(t);
            var srcExpected = new ExpectedModule(k, m.Definition!, srcSettings);
            var tgtExpected = new ExpectedModule(k, t.Definition!, tgtSettings);

            // forward drops the TARGET's object, the rollback drops what forward created (the source's kind)
            var forward = ScriptText.ModHdr(m) + FormattingGenerator.Swap(Ddl.DropWord(t.OType)!, k, m.Definition!);
            var rollback = ScriptText.ModHdr(t) + FormattingGenerator.Swap(Ddl.DropWord(m.OType)!, k, t.Definition!);
            ctx.Writer.WriteSql(Folder, $"objects\\{file}", forward, WrittenFileKind.ExecBlockModules,
                obj: k, settings: srcSettings, expectedModules: [srcExpected]);
            ctx.Writer.WriteSql(Folder, $"rollback\\{file}", rollback, WrittenFileKind.ExecBlockModules,
                obj: k, settings: tgtSettings, expectedModules: [tgtExpected]);
            fwd.Append(forward);
            back.Append(rollback);
            fwdExpected.Add(srcExpected);
            backExpected.Add(tgtExpected);
            ctx.Facts.DifferentModules.Add(k);

            var check = new List<string>(ModuleSignature.CallerChanges(m, t));
            if (m.AnsiNulls != t.AnsiNulls || m.QuotedId != t.QuotedId)
                check.Add($"ANSI_NULLS / QUOTED_IDENTIFIER change to {srcLabel}'s - this changes how NULL comparisons and quoted names behave");
            if (dates is not null && dates.Value.Src.TryGetValue(k, out var sd) && dates.Value.Tgt.TryGetValue(k, out var td) && td > sd)
                check.Add($"{tgtLabel} changed it on {Day(td)}, after {srcLabel} last did ({Day(sd)}) - it may hold a fix made only in {tgtLabel}");
            if (notes.TryGetValue(k, out var extra)) check.AddRange(extra);
            ctx.DifferentCandidates.TryGetValue(k.ToString(), out var lines);
            rows.Add(new Row(number, file, k, m.OType.Trim(), lines, qs.RunsFor(k.Name), check));
        }
        ctx.Writer.WriteSql(Folder, "_ALL.sql", fwd.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: fwdExpected);
        ctx.Writer.WriteSql(Folder, "rollback\\_ALL.sql", back.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: backExpected);
        ctx.Writer.WriteRunner(Folder, ctx.RunnerServer);
        ctx.Review.SetDocument(ReviewLog.DifferentModulesFile, Report(ctx, rows, qs, dates is null ? "module change dates" : null));

        result.Files = ctx.Writer.ObjectFileCount(Folder);
        result.HasRollback = true;
        var kinds = kept.GroupBy(m => FormattingGenerator.KindWord(m.OType))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        result.Notes.Add($"{string.Join(", ", kinds)}.");
        var flagged = rows.Count(r => r.Check.Count > 0);
        result.Notes.Add($"Replaces {tgtLabel}'s own code: read 99_review\\{ReviewLog.DifferentModulesFile} first" +
                         (flagged > 0 ? $" - {flagged} need a check (callers or a newer {tgtLabel} version)." : "."));
        result.Notes.Add(qs.Available && qs.RunsByName.Count > 0
            ? $"Least-used first, busiest last (Query Store runs on {tgtLabel}, {qs.WindowText}), so a run can stop before the busy ones."
            : $"Query Store usage was not available on {tgtLabel}, so the order is alphabetical.");
        if (!forcedChecked)
            result.Notes.Add("Forced Query Store plans could not be read; replacing a module un-forces its plan.");
        if (flagged > 0)
            ctx.Review.Warn($"10_different_modules: {flagged} replaced module(s) change their parameters or may overwrite a fix made only in {tgtLabel} - see 99_review\\{ReviewLog.DifferentModulesFile}.");
        return [result];
    }

    private sealed record Row(int Number, string File, ObjectName Name, string OType, LineChange? Lines, long Runs, List<string> Check);

    private static async Task<(Dictionary<ObjectName, DateTime> Src, Dictionary<ObjectName, DateTime> Tgt)?> ModuleDatesAsync(ScriptContext ctx)
    {
        var s = await ctx.SourceProbes.ModuleDatesAsync(ctx.Ct);
        var t = await ctx.Probes.ModuleDatesAsync(ctx.Ct);
        if (s is null || t is null) return null;
        static Dictionary<ObjectName, DateTime> Map(IEnumerable<(ObjectName Name, DateTime Modified)> rows)
        {
            var d = new Dictionary<ObjectName, DateTime>(ObjectName.IgnoreCase);
            foreach (var (name, modified) in rows) d[name] = modified;
            return d;
        }
        return (Map(s), Map(t));
    }

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Report(ScriptContext ctx, List<Row> rows, QueryStoreUsage qs, string? notRead)
    {
        var src = ctx.SourceEndpoint.Label;
        var tgt = ctx.TargetEndpoint.Label;
        var sb = new StringBuilder();
        sb.Append($"# Modules 10_different_modules replaces\n\n");
        if (rows.Count == 0)
        {
            sb.Append($"None. Modules with different code that were left alone are in `{ReviewLog.DifferentNotApplied}`.\n");
            return sb.ToString();
        }
        sb.Append($"{rows.Count} procedures, views, functions or triggers have different code in {src} and {tgt}. Each file replaces ");
        sb.Append($"{tgt}'s version with {src}'s. **{tgt}'s version may hold a change made only there** (a site fix) - compare the two texts ");
        sb.Append("in SchemaScope before running, and leave out any file you are not sure of. `rollback\\` holds the current text of each.\n\n");
        sb.Append("Each file runs in one transaction: if the CREATE fails, the old version stays. Run this folder after RUN.ps1 ");
        sb.Append("(new code may need the new columns and objects), least-used first; the busiest are last.\n\n");
        if (notRead is not null) sb.Append($"> {notRead} could not be read, so a newer {tgt} version is not flagged.\n\n");
        var flagged = rows.Count(r => r.Check.Count > 0);
        sb.Append($"- needs a check: {flagged}\n- nothing found to check: {rows.Count - flagged}\n\n");
        sb.Append($"| # | File | Object | Type | Lines changed | Runs on {Cell(tgt)} | Check before running |\n|---:|---|---|---|---:|---:|---|\n");
        foreach (var r in rows)
        {
            var lines = r.Lines is null ? "-" : $"+{r.Lines.Added} / -{r.Lines.Removed}";
            var runs = qs.Available ? r.Runs.ToString("N0", CultureInfo.InvariantCulture) : "-";
            var check = r.Check.Count == 0 ? "-" : string.Join("<br>", r.Check.Select(Cell));
            sb.Append($"| {r.Number} | {Cell(r.File)} | {Cell(r.Name.ToString())} | {FormattingGenerator.KindWord(r.OType)} | {lines} | {runs} | {check} |\n");
        }
        return sb.ToString();
    }

    private static string Cell(string s) => ReviewLog.Cell(s);
}
