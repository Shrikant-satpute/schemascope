using System.Globalization;
using System.Text;

namespace SchemaScope.Scripting;

/// <summary>
/// One run of folders 01–06 for <see cref="CoreFoldersGenerator"/>. Every fact is computed first, whatever
/// is selected, then the selected folders are written. The compute order is 01, 03, 02, 05, 06, 04: 03 needs
/// the columns 01 does not add (a module that names one fails at CREATE), 05 needs 03's new tables and 01's
/// columns, and generate.py recorded its held-back rows as 05, 06, then 04, so not_applied_constraints.md keeps
/// the Python's row order. (generate.py computed 03 before 01; only the order of rows in not_applied.md differs.)
/// </summary>
/// <remarks>
/// Names: the target was aligned to the source's spelling (<see cref="ScriptSchema.AlignCase"/>), so every
/// statement uses the SOURCE spelling, as the Python did. Sets this class builds itself fold case only when
/// both databases are case-insensitive (<see cref="ci"/>); on a case-sensitive database <c>Foo</c> and
/// <c>FOO</c> stay two objects. Column names are always compared lower-cased, as in the Python.
/// </remarks>
internal sealed partial class CoreRun
{
    private readonly ScriptContext ctx;
    private readonly ScriptSchema src;
    private readonly ScriptSchema tgt;
    private readonly CoreFacts facts;
    private readonly string coll;
    private readonly string srcLabel;
    private readonly string tgtLabel;
    private readonly bool ci;
    /// <summary>Tables both sides have, in Python <c>sorted(dev.tables &amp; prod.tables)</c> order, source spelling.</summary>
    private readonly List<ObjectName> commonTables;
    private Task<Dictionary<ObjectName, string>?>? targetObjects;

    public CoreRun(ScriptContext ctx)
    {
        this.ctx = ctx;
        src = ctx.Source;
        tgt = ctx.Target;
        facts = ctx.Facts;
        coll = ctx.SourceCollation;
        srcLabel = ctx.SourceEndpoint.Label;
        tgtLabel = ctx.TargetEndpoint.Label;
        ci = src.IsCaseInsensitive && tgt.IsCaseInsensitive;
        commonTables = src.SortedTables().Where(t => tgt.Tables.Contains(t)).ToList();
    }

    private CancellationToken Ct => ctx.Ct;

    public async Task<IReadOnlyList<FolderResult>> RunAsync()
    {
        DescribeReviewFiles();

        ctx.Report(ScriptGenerator.P.Prereqs, "Checking what the target already has");
        await LoadPrereqsAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.Columns, "01 New columns");
        await ComputeNewColumnsAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.Missing, "03 Missing objects - compile checks");
        await ComputeMissingAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.TableTypes, "02 Table types");
        await ComputeTableTypesAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.Constraints, "05 Constraints - checking existing data");
        await ComputeConstraintsAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.Indexes, "06 Indexes - checking existing data");
        await ComputeIndexesAsync();
        Ct.ThrowIfCancellationRequested();
        ctx.Report(ScriptGenerator.P.ColumnTypes, "04 Column types");
        await ComputeColumnTypesAsync();
        ctx.Report(ScriptGenerator.P.Write, "Writing folders 01-06");

        // a cancelled run stops between folders, not after writing all of them
        var results = new List<FolderResult>();
        void Next() => Ct.ThrowIfCancellationRequested();
        if (ctx.Selected(ScriptSection.NewColumns)) { Next(); results.Add(WriteColumns()); }
        if (ctx.Selected(ScriptSection.TableTypes)) { Next(); results.Add(await WriteTableTypesAsync()); }
        if (ctx.Selected(ScriptSection.MissingObjects)) { Next(); results.Add(WriteMissing()); }
        if (ctx.Selected(ScriptSection.ColumnTypes)) { Next(); results.Add(WriteColumnTypes()); }
        if (ctx.Selected(ScriptSection.Constraints)) { Next(); results.Add(WriteConstraints()); }
        if (ctx.Selected(ScriptSection.Indexes)) { Next(); results.Add(WriteIndexes()); }
        return results;
    }

    private void DescribeReviewFiles()
    {
        if (ctx.Selected(ScriptSection.ColumnTypes) || ctx.Selected(ScriptSection.Constraints) || ctx.Selected(ScriptSection.Indexes))
            ctx.Review.Describe(ReviewLog.NotAppliedConstraints, "Constraints and indexes deliberately not applied",
                $"Each one would either overwrite a rule {tgtLabel} already has, or be rejected by data {tgtLabel} already holds. " +
                "Both need a decision, so no script is generated for them.");
        if (ctx.Selected(ScriptSection.MissingObjects))
            ctx.Review.Describe(ReviewLog.QuarantinedNotApplied, "Left out of 03_missing - quarantined or excluded",
                $"{srcLabel} holds them, but each name matches a .sql file under the quarantine folder or an exclude pattern, " +
                $"so 03_missing does not create it. Kind is the object type in {srcLabel} (U = table, TT = table type).");
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Python <c>%03d</c>.</summary>
    private static string N3(int n) => n.ToString("D3", CultureInfo.InvariantCulture);

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>A schema or table name as a set key: folded only when both databases ignore case.</summary>
    private string Fold(string s) => ci ? s.ToLowerInvariant() : s;

    private string TableKey(string sch, string tbl) => Fold(sch) + "\0" + Fold(tbl);

    private string TableKey(ObjectName t) => TableKey(t.Schema, t.Name);

    /// <summary>generate.py's <c>(sch, tbl, col.lower())</c>.</summary>
    private string ColumnKey(string sch, string tbl, string col) => TableKey(sch, tbl) + "\0" + col.ToLowerInvariant();

    private void Hold(string file, string kind, string name, string? table, string reason, string folder) =>
        ctx.Review.Hold(file, kind, name, table, reason, folder);

    private const string ExcludedReason = "matches an exclude pattern";

    /// <summary>
    /// The target's sys.objects keyed by (schema, name), loaded once. Tables, views, modules and constraints
    /// share one namespace per schema, so a name taken by an object of another type makes CREATE fail (Msg 2714).
    /// Null when the probe failed (the probe records the "not checked" note).
    /// </summary>
    private Task<Dictionary<ObjectName, string>?> TargetObjectsAsync() => targetObjects ??= LoadTargetObjectsAsync();

    private async Task<Dictionary<ObjectName, string>?> LoadTargetObjectsAsync()
    {
        var rows = await ctx.Probes.ObjectsAsync(Ct);
        if (rows is null) return null;
        var d = new Dictionary<ObjectName, string>(tgt.NameComparer);
        foreach (var o in rows) d.TryAdd(o.Name, o.Type);
        return d;
    }

    /// <summary>A readable word for a sys.objects type code, for review reasons.</summary>
    private static string TypeWord(string code) => code.Trim().ToUpperInvariant() switch
    {
        "U" => "table",
        "V" => "view",
        "P" => "procedure",
        "PC" => "CLR procedure",
        "FN" or "IF" or "TF" => "function",
        "FS" or "FT" => "CLR function",
        "AF" => "CLR aggregate",
        "TR" => "trigger",
        "TA" => "CLR trigger",
        "PK" => "primary key",
        "UQ" => "unique constraint",
        "F" => "foreign key",
        "C" => "check constraint",
        "D" => "default constraint",
        "SO" => "sequence",
        "SN" => "synonym",
        "R" => "rule",
        "X" => "extended procedure",
        "SQ" => "service queue",
        "TT" => "table type",
        "IT" => "internal table",
        "S" => "system table",
        var other => "type " + other,
    };

    /// <summary>Creates <c>folder\objects\</c> so an empty folder still has the directory run_all.ps1 lists.</summary>
    private void EnsureObjects(string folder) => Directory.CreateDirectory(ctx.Writer.Resolve(folder, "objects"));

    /// <summary>
    /// A folder of plain DDL statements (01, 04, 05, 06): each <c>objects\</c> file is
    /// <c>HDR_TBL + sql + "\r\nGO\r\n"</c>, <c>_ALL.sql</c> is <c>HDR_TBL</c> plus every <c>sql + "\r\nGO\r\n"</c>.
    /// </summary>
    private FolderResult WriteStatementsFolder(
        string folder, string title, IEnumerable<(string File, string Sql, ObjectName? Obj)> items, List<string> notes)
    {
        var list = items.ToList();
        if (list.Count == 0) return NothingToDo(folder, title, notes);
        EnsureObjects(folder);
        var all = new StringBuilder(ScriptText.HdrTbl);
        foreach (var (file, sql, obj) in list)
        {
            ctx.Writer.WriteSql(folder, "objects\\" + file, ScriptText.HdrTbl + sql + ScriptText.Go, WrittenFileKind.Statements, obj);
            all.Append(sql).Append(ScriptText.Go);
        }
        return FinishFolder(folder, title, all.ToString(), [], inRunAll: true, notes);
    }

    /// <summary>
    /// A selected folder with nothing to script: no directory, no _ALL.sql, no run_all.ps1 (the Python wrote an empty
    /// folder with both; 07 and 09 already wrote nothing). It is still listed, with 0 files and a note, and RUN.ps1
    /// leaves it out because it was not written.
    /// </summary>
    private static FolderResult NothingToDo(string folder, string title, List<string> notes)
    {
        notes.Insert(0, ScriptGenerator.NothingToDoNote);
        return new FolderResult { Folder = folder, Title = title, Files = 0, InRunAll = false, HasRollback = false, Notes = notes };
    }

    /// <summary>Writes <c>_ALL.sql</c> and <c>run_all.ps1</c> and builds the folder result.</summary>
    private FolderResult FinishFolder(string folder, string title, string allText, List<ExpectedModule> modules, bool inRunAll, List<string> notes)
    {
        ctx.Writer.WriteSql(folder, "_ALL.sql", allText,
            modules.Count > 0 ? WrittenFileKind.ExecBlockModules : WrittenFileKind.Statements,
            expectedModules: modules.Count > 0 ? modules : null);
        ctx.Writer.WriteRunner(folder, ctx.RunnerServer);
        return new FolderResult
        {
            Folder = folder,
            Title = title,
            Files = ctx.Writer.ObjectFileCount(folder),
            InRunAll = inRunAll,
            HasRollback = false,
            Notes = notes,
        };
    }

    /// <summary>
    /// A per-object module file. Normally <c>MODHDR + definition</c> with nothing after it (generate.py: SQL Server
    /// stores the whole batch as the definition, and sqlcmd runs the last batch at end of file). When the raw text
    /// cannot travel in a file byte-exactly - a NUL or unpaired surrogate (<see cref="ScriptText.RawModuleProblem"/>),
    /// or a line SSMS would take as GO / a sqlcmd command - the module goes through <see cref="ScriptText.ExecBlock"/>
    /// instead, which rebuilds the exact text inside the batch.
    /// </summary>
    /// <param name="m">The module whose SET options apply (source row for 03, target row for 02's recreate).</param>
    /// <param name="definition">The exact text the module must end up with.</param>
    private void WriteModuleFile(string folder, string relativePath, ModuleRow m, string definition)
    {
        var settings = ModuleSettings.Of(m);
        if (ScriptText.RawModuleProblem(definition) is null && !ScriptText.HasBatchBreakingLine(definition))
        {
            ctx.Writer.WriteSql(folder, relativePath, ScriptText.ModHdr(m) + definition, WrittenFileKind.RawModule,
                m.Name, expectedModuleText: definition, settings: settings);
            return;
        }
        ctx.Writer.WriteSql(folder, relativePath, ScriptText.ModHdr(m) + ScriptText.ExecBlock(definition) + "GO\r\n",
            WrittenFileKind.ExecBlockModules, m.Name, settings: settings,
            expectedModules: [new ExpectedModule(m.Name, definition, settings)]);
    }

    /// <summary>
    /// One module inside an <c>_ALL.sql</c> that starts with <c>HDR_TBL</c> (both options ON):
    /// <c>exec_block(def) + "GO\r\n"</c>, wrapped in <c>SET … OFF</c> / <c>SET … ON</c> for each option the module was
    /// created OFF. generate.py wrapped only QUOTED_IDENTIFIER; ANSI_NULLS OFF is wrapped the same way (python.md §1
    /// calls the missing wrap a bug). For a QUOTED_IDENTIFIER-only module the text is the Python's byte for byte.
    /// </summary>
    private static string AllChunk(ModuleRow m, string definition)
    {
        var sb = new StringBuilder();
        if (!m.AnsiNulls) sb.Append("SET ANSI_NULLS OFF\r\nGO\r\n");
        if (!m.QuotedId) sb.Append("SET QUOTED_IDENTIFIER OFF\r\nGO\r\n");
        sb.Append(ScriptText.ExecBlock(definition)).Append("GO\r\n");
        if (!m.QuotedId) sb.Append("SET QUOTED_IDENTIFIER ON\r\nGO\r\n");
        if (!m.AnsiNulls) sb.Append("SET ANSI_NULLS ON\r\nGO\r\n");
        return sb.ToString();
    }

    /// <summary>Rows of a <see cref="ScriptSchema.ByTable{T}"/> grouping for one table, or none.</summary>
    private static IReadOnlyList<T> Of<T>(Dictionary<ObjectName, List<T>> byTable, ObjectName table) =>
        byTable.TryGetValue(table, out var l) ? l : [];
}
