using System.Text;

namespace SchemaScope.Scripting;

/// <summary>
/// Folders 08_formatting_modules and 09_formatting_tables plus formatting_not_applied.md (port of generate_formatting.py). Every replaced or renamed object gets a rollback file.
/// </summary>
/// <remarks>
/// SchemaScope calls more things "formatting" than are safe to overwrite: it ignores letter case
/// everywhere, inside string literals too. 08 therefore re-checks every module SchemaScope marked
/// FormattingOnly with the Python's strict token classifier: string literals must match exactly
/// (<c>'$.Field'</c> in OPENJSON is case-sensitive), ANSI_NULLS / QUOTED_IDENTIFIER must match (they
/// change how the code runs), and a name whose case changes must be a keyword, a variable, a temp
/// table, a schema or a database object - anything else may be a column name the caller receives
/// (a FOR JSON key, a DataReader column) and goes to review. The Python's hand-kept VERIFIED_INTERNAL
/// allow-list is gone: those land in review too and the user decides.
/// 09 renames tables, columns, constraints and indexes whose names differ only in letter case.
/// Neither folder is in RUN.ps1.
/// </remarks>
public sealed class FormattingGenerator : IFolderGenerator
{
    /// <summary>The module folder.</summary>
    public const string ModulesFolder = "08_formatting_modules";

    /// <summary>The rename folder.</summary>
    public const string TablesFolder = "09_formatting_tables";

    private const string CodeDiffers = "code differs";

    public async Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx)
    {
        ctx.Review.Describe(ReviewLog.FormattingNotApplied,
            "\"Formatting\" differences deliberately not made the same",
            "SchemaScope reports these as formatting only, because it ignores letter case everywhere - inside text values too. " +
            $"Each one below can change what {ctx.TargetEndpoint.Label} stores or returns, or cannot be replaced safely, " +
            "so no script is generated. Read both texts and change them by hand if you are sure.");

        var modules = await ModulesAsync(ctx);
        ctx.Ct.ThrowIfCancellationRequested();
        var tables = await TablesAsync(ctx);
        return [modules, tables];
    }

    // =================================================================================== 08

    private static async Task<FolderResult> ModulesAsync(ScriptContext ctx)
    {
        var ct = ctx.Ct;
        var result = new FolderResult { Folder = ModulesFolder, Title = "Formatting-only modules", InRunAll = false };
        var objects = await KnownNamesAsync(ctx);

        // ---- candidates: SchemaScope's FormattingOnly set, on both sides, really different, not Same by Light
        var safe = new List<ObjectName>();
        foreach (var k in ctx.Source.SortedModules())
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.FormattingCandidates.Contains(k.ToString()) || !ctx.InScope(k)) continue;
            var src = ctx.Source.Modules[k];
            if (ctx.Target.FindModule(k) is not { } tgt) continue;
            if (src.Definition is null || tgt.Definition is null)
            {
                HoldModule(ctx, k, src, "no definition on one side (encrypted or CLR) - cannot be compared or copied");
                continue;
            }
            var a = src.Definition;
            var b = tgt.Definition;
            if (string.Equals(a, b, StringComparison.Ordinal) ||
                string.Equals(TsqlTokens.Light(a), TsqlTokens.Light(b), StringComparison.Ordinal))
                continue;                                   // SchemaScope already reports Same

            var why = Classify(src, tgt, objects);
            if (why == CodeDiffers) continue;               // a real difference, not formatting
            if (why is not null)
            {
                HoldModule(ctx, k, src, why);
                continue;
            }
            if (ctx.IsExcluded(k))
            {
                HoldModule(ctx, k, src, "matches an exclude pattern");
                continue;
            }
            if (Ddl.DropWord(src.OType) is null || Ddl.DropWord(tgt.OType) is null)
            {
                HoldModule(ctx, k, src, $"object type {src.OType} cannot be replaced by DROP + CREATE");
                continue;
            }
            safe.Add(k);
        }

        // ---- drop-safety guards on the target (keyed by lower-cased name, schema ignored - the conservative direction)
        var guards = await ctx.Probes.FormattingGuardsAsync(includeExtraGuards: true, ct);
        var blindGuards = await UncheckedGuardsAsync(ctx);
        var forcedChecked = await ctx.Probes.ForcedPlanNamesAsync(ct) is not null;
        var kept = new List<ObjectName>();
        foreach (var k in safe)
        {
            var src = ctx.Source.Modules[k];
            var tgt = ctx.Target.FindModule(k)!;
            var missingChecks = blindGuards
                .Where(u => u.AppliesTo(tgt.OType))
                .Select(u => u.What)
                .ToList();
            if (guards.TryGetValue(k.Name.ToLowerInvariant(), out var reasons) && reasons.Count > 0)
            {
                HoldModule(ctx, k, src, string.Join("; ", reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
                continue;
            }
            if (missingChecks.Count > 0)
            {
                HoldModule(ctx, k, src,
                    $"could not check on {ctx.TargetEndpoint.Label} whether DROP would lose something ({string.Join(", ", missingChecks)} not readable)");
                continue;
            }
            kept.Add(k);
        }

        // ---- least used first, busiest last, so a run can stop before the hot procedures
        var qs = await ctx.Probes.QueryStoreAsync(ct);
        kept = kept
            .OrderBy(k => qs.RunsFor(k.Name))
            .ThenBy(k => k.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(k => k.Schema.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        if (kept.Count == 0)
        {
            result.Notes.Insert(0, ScriptGenerator.NothingToDoNote);
            result.Notes.Add("No module qualified.");
            return result;
        }

        var fwd = new StringBuilder();
        var back = new StringBuilder();
        var fwdExpected = new List<ExpectedModule>();
        var backExpected = new List<ExpectedModule>();
        var number = 0;
        foreach (var k in kept)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            var src = ctx.Source.Modules[k];
            var tgt = ctx.Target.FindModule(k)!;
            var file = $"{number:000}_{ScriptText.Safe(k.Name)}.sql";
            var srcSettings = ModuleSettings.Of(src);
            var tgtSettings = ModuleSettings.Of(tgt);
            var srcExpected = new ExpectedModule(k, src.Definition!, srcSettings);
            var tgtExpected = new ExpectedModule(k, tgt.Definition!, tgtSettings);

            // forward drops the TARGET's object, the rollback drops what forward created (the source's kind)
            var forward = ScriptText.ModHdr(src) + Swap(Ddl.DropWord(tgt.OType)!, k, src.Definition!);
            var rollback = ScriptText.ModHdr(tgt) + Swap(Ddl.DropWord(src.OType)!, k, tgt.Definition!);

            ctx.Writer.WriteSql(ModulesFolder, $"objects\\{file}", forward, WrittenFileKind.ExecBlockModules,
                obj: k, settings: srcSettings, expectedModules: [srcExpected]);
            ctx.Writer.WriteSql(ModulesFolder, $"rollback\\{file}", rollback, WrittenFileKind.ExecBlockModules,
                obj: k, settings: tgtSettings, expectedModules: [tgtExpected]);
            fwd.Append(forward);
            back.Append(rollback);
            fwdExpected.Add(srcExpected);
            backExpected.Add(tgtExpected);
            ctx.Facts.FormattingModules.Add(k);
        }
        ctx.Writer.WriteSql(ModulesFolder, "_ALL.sql", fwd.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: fwdExpected);
        ctx.Writer.WriteSql(ModulesFolder, "rollback\\_ALL.sql", back.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: backExpected);
        ctx.Writer.WriteRunner(ModulesFolder, ctx.RunnerServer);

        result.Files = ctx.Writer.ObjectFileCount(ModulesFolder);
        result.HasRollback = true;
        var kinds = kept.GroupBy(k => KindWord(ctx.Source.Modules[k].OType))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        result.Notes.Add($"{string.Join(", ", kinds)}.");
        result.Notes.Add(qs.Available && qs.RunsByName.Count > 0
            ? $"Least-used first, busiest last (Query Store runs on {ctx.TargetEndpoint.Label}, {qs.WindowText}), so a run can stop before the busy ones."
            : $"Query Store usage was not available on {ctx.TargetEndpoint.Label}, so the order is alphabetical.");
        if (!forcedChecked)
            result.Notes.Add("Forced Query Store plans could not be read; replacing a module un-forces its plan.");
        return result;
    }

    /// <summary>
    /// generate_formatting.py's transactional swap, verbatim: DROP + CREATE in one transaction, so callers
    /// wait on the schema lock instead of finding the object gone, and a failed CREATE puts the original back.
    /// DROP + CREATE is the only way to store the source's exact bytes, because its text starts with CREATE.
    /// </summary>
    internal static string Swap(string dropWord, ObjectName name, string text) =>
        "SET XACT_ABORT ON;\r\nBEGIN TRY\r\nBEGIN TRANSACTION;\r\n" +
        $"DROP {dropWord} {name.Bracketed};\r\n" +
        ScriptText.ExecBlock(text) +
        "COMMIT TRANSACTION;\r\nEND TRY\r\nBEGIN CATCH\r\n" +
        "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\r\nTHROW;\r\nEND CATCH\r\nGO\r\n";

    /// <summary>
    /// generate_formatting.py <c>classify</c>: null when the two texts differ only in harmless formatting,
    /// <see cref="CodeDiffers"/> when the tokens really differ, otherwise the reason for review.
    /// </summary>
    private static string? Classify(ModuleRow source, ModuleRow target, IReadOnlySet<string> objects)
    {
        var ta = TsqlTokens.Tokens(source.Definition!);
        var tb = TsqlTokens.Tokens(target.Definition!);
        if (ta.Count != tb.Count) return CodeDiffers;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? literal = null;
        for (var i = 0; i < ta.Count; i++)
        {
            var x = ta[i];   // source
            var y = tb[i];   // target
            if (x.Kind != y.Kind || !string.Equals(x.Text.ToLowerInvariant(), y.Text.ToLowerInvariant(), StringComparison.Ordinal))
                return CodeDiffers;
            if (string.Equals(x.Text, y.Text, StringComparison.Ordinal)) continue;
            if (x.Kind == 'L')
            {
                literal ??= $"a text value differs only in letter case: {Head(y.Text, 60)} vs {Head(x.Text, 60)}";
                continue;
            }
            var w = x.Text.ToLowerInvariant();
            if (TsqlTokens.Keywords.Contains(w) || w.StartsWith('@') || w.StartsWith('#') || objects.Contains(w)) continue;
            seen.Add($"{y.Text} / {x.Text}");
        }
        if (literal is not null) return literal;
        if (source.AnsiNulls != target.AnsiNulls || source.QuotedId != target.QuotedId)
            return "ANSI_NULLS / QUOTED_IDENTIFIER differ - these change how the code runs";
        if (seen.Count > 0)
            return "letter case of a name the caller may receive: " + string.Join(", ", seen.Order(StringComparer.Ordinal).Take(4));
        return null;
    }

    /// <summary>
    /// Names whose case may change freely: module, table and table-type names of both sides, every schema
    /// of both sides (the Python had only dbo), and inserted/deleted. Lower-cased.
    /// </summary>
    private static async Task<IReadOnlySet<string>> KnownNamesAsync(ScriptContext ctx)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { "inserted", "deleted", "dbo" };
        foreach (var s in new[] { ctx.Source, ctx.Target })
        {
            foreach (var k in s.Modules.Keys.Concat(s.Tables).Concat(s.TableTypes))
            {
                set.Add(k.Name.ToLowerInvariant());
                set.Add(k.Schema.ToLowerInvariant());
            }
        }
        foreach (var n in await ctx.SourceProbes.SchemaNamesAsync(ctx.Ct) ?? []) set.Add(n.ToLowerInvariant());
        foreach (var n in await ctx.Probes.SchemaNamesAsync(ctx.Ct) ?? []) set.Add(n.ToLowerInvariant());
        return set;
    }

    /// <summary>A guard probe that could not run, and which module kinds it protects.</summary>
    internal sealed record UncheckedGuard(string What, string[]? Types)
    {
        public bool AppliesTo(string otype) => Types is null || Types.Contains(otype.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The guards whose probe failed. A guard that could not be read protects nothing, so a module it
    /// would cover is held back rather than replaced blind. Forced plans are left out (losing one costs
    /// performance, not data or rights): they only get a folder note.
    /// </summary>
    internal static async Task<List<UncheckedGuard>> UncheckedGuardsAsync(ScriptContext ctx)
    {
        var p = ctx.Probes;
        var ct = ctx.Ct;
        var list = new List<UncheckedGuard>();
        if (await p.ObjectPermissionNamesAsync(ct) is null) list.Add(new("object permissions", null));
        if (await p.ExtendedPropertyNamesAsync(ct) is null) list.Add(new("extended properties", null));
        if (await p.ExpressionDependentsAsync(ct) is null) list.Add(new("schema-bound dependencies", null));
        if (await p.SignedModuleNamesAsync(ct) is null) list.Add(new("module signatures", null));
        if (await p.IndexedViewNamesAsync(ct) is null) list.Add(new("indexed views", ["V"]));
        if (await p.DisabledTriggersAsync(ct) is null) list.Add(new("disabled triggers", ["TR"]));
        if (await p.OrderedTriggerNamesAsync(ct) is null) list.Add(new("trigger order", ["TR"]));
        return list;
    }

    private static void HoldModule(ScriptContext ctx, ObjectName k, ModuleRow m, string reason) =>
        ctx.Review.Hold(ReviewLog.FormattingNotApplied, KindWord(m.OType), k.ToString(), null, reason, ModulesFolder);

    internal static string KindWord(string otype) => otype.Trim().ToUpperInvariant() switch
    {
        "P" => "procedure",
        "FN" or "IF" or "TF" => "function",
        "V" => "view",
        "TR" => "trigger",
        var o => o.ToLowerInvariant(),
    };

    /// <summary>Python's <c>s[:n]</c>: the first n code points (a surrogate pair is one).</summary>
    private static string Head(string s, int n)
    {
        int i = 0, count = 0;
        while (i < s.Length && count < n)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            count++;
        }
        return s[..i];
    }

    // =================================================================================== 09

    private static async Task<FolderResult> TablesAsync(ScriptContext ctx)
    {
        var ct = ctx.Ct;
        var result = new FolderResult { Folder = TablesFolder, Title = "Letter-case renames", InRunAll = false };
        var src = ctx.Source;
        var raw = ctx.RawTarget;

        if (!src.IsCaseInsensitive || !raw.IsCaseInsensitive)
        {
            result.Notes.Add(ScriptGenerator.NothingToDoNote);
            result.Notes.Add("A database uses a case-sensitive collation, so names that differ in letter case are different objects there; nothing was renamed.");
            return result;
        }

        // ---- every case-only difference, target spelling vs source spelling (generate_formatting.py:231-257)
        var srcTables = new Dictionary<ObjectName, ObjectName>(ObjectName.IgnoreCase);
        foreach (var t in src.Tables) srcTables[t] = t;
        var renames = new List<Rename>();
        var excluded = new List<ObjectName>();
        foreach (var t in raw.SortedTables())
        {
            ct.ThrowIfCancellationRequested();
            if (!srcTables.TryGetValue(t, out var hit) || !ctx.InScope(hit)) continue;
            var found = new List<Rename>();
            if (!string.Equals(hit.Name, t.Name, StringComparison.Ordinal))
                found.Add(new Rename("table", t.Schema, t.Name, t.Name, hit.Name));

            var srcCols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in src.ColumnsOf(hit)) srcCols[c.Col] = c.Col;
            foreach (var c in raw.ColumnsOf(t))
                if (srcCols.TryGetValue(c.Col, out var d) && !string.Equals(d, c.Col, StringComparison.Ordinal))
                    found.Add(new Rename("column", t.Schema, t.Name, c.Col, d));

            Named(found, t, hit, raw.Keys, src.Keys, r => r.CName, r => r.IsSystemNamed, "object");
            Named(found, t, hit, raw.FKeys, src.FKeys, r => r.CName, r => r.IsSystemNamed, "object");
            Named(found, t, hit, raw.Checks, src.Checks, r => r.CName, r => r.IsSystemNamed, "object");
            Named(found, t, hit, raw.Defaults, src.Defaults, r => r.CName, r => r.IsSystemNamed, "object");
            Named(found, t, hit, raw.Indexes, src.Indexes, r => r.IName, _ => false, "index");

            if (found.Count == 0) continue;
            if (ctx.IsExcluded(t) || ctx.IsExcluded(hit)) { excluded.Add(t); continue; }
            renames.AddRange(found);
        }
        foreach (var t in excluded)
            ctx.Review.Hold(ReviewLog.FormattingNotApplied, "table", t.ToString(), null,
                "letter-case renames not scripted - the table matches an exclude pattern", TablesFolder);

        // ---- filters: schema-bound referencers refuse sp_rename; CDC / replication / temporal are left alone
        var bound = await ctx.Probes.SchemaBoundReferencersAsync(ct);
        var boundBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in bound ?? [])
        {
            if (d.Referenced.Length == 0) continue;
            if (!boundBy.TryGetValue(d.Referenced, out var by)) boundBy[d.Referenced] = by = new SortedSet<string>(StringComparer.Ordinal);
            by.Add(d.ReferencedBy);
        }
        var special = await ctx.Probes.SpecialTableNamesAsync(ct);
        var specialSet = new HashSet<string>(special ?? [], StringComparer.OrdinalIgnoreCase);

        var ok = new List<Rename>();
        foreach (var r in renames)
        {
            string? why = null;
            if (boundBy.TryGetValue(r.Table, out var by))
                why = $"rename refused - schema-bound by {string.Join(", ", by)}";
            else if (special is null)
                why = $"could not check on {ctx.TargetEndpoint.Label} whether the table is replicated, CDC-tracked or temporal";
            else if (specialSet.Contains(r.Table))
                why = "rename skipped - table is replicated, CDC-tracked or temporal";
            if (why is null) { ok.Add(r); continue; }
            var tableName = $"{r.Schema}.{r.Table}";
            ctx.Review.Hold(ReviewLog.FormattingNotApplied, $"{r.Kind} rename",
                r.Kind == "table" ? $"{tableName} → {r.New}" : $"{r.Old} → {r.New}",
                r.Kind == "table" ? null : tableName, why, TablesFolder);
        }

        // rename the table last, so every other statement can still use the old table name
        ok = ok.OrderBy(r => r.Kind == "table")
            .ThenBy(r => r.Table, StringComparer.Ordinal)
            .ThenBy(r => r.Old, StringComparer.Ordinal)
            .ToList();

        if (bound is null)
            result.Notes.Add("Schema-bound references could not be read; a rename on such a table is refused by SQL Server when run (nothing changes).");
        if (ok.Count == 0)
        {
            result.Notes.Insert(0, ScriptGenerator.NothingToDoNote);
            result.Notes.Add("No name differs only in letter case.");
            return result;
        }

        var fwd = new StringBuilder();
        var backs = new List<string>();
        var number = 0;
        foreach (var r in ok)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            var file = $"{number:000}_{r.Kind}_{ScriptText.Safe(r.Old)}.sql";
            var forward = RenameSql(r.Kind, r.Schema, r.Table, r.Old, r.New);
            var rollback = RenameSql(r.Kind, r.Schema, r.Kind == "table" ? r.New : r.Table, r.New, r.Old);
            var obj = new ObjectName(r.Schema, r.Table);
            ctx.Writer.WriteSql(TablesFolder, $"objects\\{file}", forward, WrittenFileKind.Statements, obj: obj);
            ctx.Writer.WriteSql(TablesFolder, $"rollback\\{file}", rollback, WrittenFileKind.Statements, obj: obj);
            fwd.Append(forward);
            backs.Insert(0, rollback);                      // reverse order: the table name is restored first
            ctx.Facts.Renames.Add(new CaseRenameItem(number, file, r.Kind, r.Schema, r.Table, r.Old, r.New));
        }
        ctx.Writer.WriteSql(TablesFolder, "_ALL.sql", fwd.ToString(), WrittenFileKind.Statements);
        ctx.Writer.WriteSql(TablesFolder, "rollback\\_ALL.sql", string.Concat(backs), WrittenFileKind.Statements);
        ctx.Writer.WriteRunner(TablesFolder, ctx.RunnerServer);

        result.Files = ctx.Writer.ObjectFileCount(TablesFolder);
        result.HasRollback = true;
        var kinds = ok.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {(g.Key == "object" ? "constraint" : g.Key)}{(g.Count() == 1 ? "" : "s")}");
        result.Notes.Add($"{string.Join(", ", kinds)}.");
        var columns = ok.Count(r => r.Kind == "column");
        if (columns > 0)
            result.Notes.Add($"{columns} column rename{(columns == 1 ? " changes" : "s change")} the letter case of a column name callers receive " +
                             "(SELECT *, FOR JSON keys, DataReader columns) - check the application before running.");
        return result;
    }

    /// <summary>One case-only rename (generate_formatting.py's <c>(kind, schema, table, old, new)</c>); Kind is table, column, index or object.</summary>
    private sealed record Rename(string Kind, string Schema, string Table, string Old, string New);

    /// <summary>
    /// Constraint/index renames on one table: a target row whose name matches a source row of the same
    /// table ignoring case but is spelled differently. System-named constraints are skipped (their names are
    /// generated, not chosen). The Python matched rows by table name only; here the schema must match too.
    /// </summary>
    private static void Named<T>(List<Rename> output, ObjectName table, ObjectName sourceTable,
        IReadOnlyList<T> targetRows, IReadOnlyList<T> sourceRows, Func<T, string?> name, Func<T, bool> systemNamed, string kind)
        where T : ITableRow
    {
        var srcNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in sourceRows)
            if (new ObjectName(r.Sch, r.Tbl).SameAs(sourceTable) && name(r) is { Length: > 0 } n) srcNames[n] = n;
        foreach (var r in targetRows)
        {
            if (!new ObjectName(r.Sch, r.Tbl).SameAs(table) || name(r) is not { Length: > 0 } old || systemNamed(r)) continue;
            if (srcNames.TryGetValue(old, out var d) && !string.Equals(d, old, StringComparison.Ordinal))
                output.Add(new Rename(kind, table.Schema, table.Name, old, d));
        }
    }

    /// <summary>
    /// generate_formatting.py <c>rename_sql</c>. Byte-identical for ordinary names; a <c>]</c> in a name is
    /// bracket-escaped and a <c>'</c> doubled, which the Python did not do.
    /// </summary>
    private static string RenameSql(string kind, string schema, string table, string old, string @new) => kind switch
    {
        "table" => $"EXEC sp_rename {ScriptText.Lit(Ddl.Bracket(schema, old))}, {ScriptText.Lit(@new)};\r\nGO\r\n",
        "column" => $"EXEC sp_rename {ScriptText.Lit(Ddl.Bracket(schema, table) + "." + Ddl.Bracket(old))}, {ScriptText.Lit(@new)}, N'COLUMN';\r\nGO\r\n",
        "index" => $"EXEC sp_rename {ScriptText.Lit(Ddl.Bracket(schema, table) + "." + Ddl.Bracket(old))}, {ScriptText.Lit(@new)}, N'INDEX';\r\nGO\r\n",
        _ => $"EXEC sp_rename {ScriptText.Lit(Ddl.Bracket(schema, old))}, {ScriptText.Lit(@new)}, N'OBJECT';\r\nGO\r\n",
    };
}
