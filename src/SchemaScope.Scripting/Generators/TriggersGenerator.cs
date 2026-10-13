using System.Text;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting;

/// <summary>
/// Folder 07_triggers, generic (DESIGN.md §7): source triggers missing in the target whose table exists there or is created by 03, least-busy table first, each with a rollback drop.
/// </summary>
/// <remarks>
/// The Python (generate_triggers.py) worked from a hand-kept list in a hand-picked risk order. Here the
/// list is computed: every source DML trigger the target lacks, kept only when its parent table (or view)
/// is in the target or is created by 03. The order is the same idea made automatic: target row count of
/// the parent ascending, then name - a trigger on an empty table first, the one on the busiest table last,
/// so a run can stop before it. A trigger changes what every write to its table does, so the folder is
/// never in RUN.ps1 and every file has a rollback that drops the trigger again.
/// </remarks>
public sealed partial class TriggersGenerator : IFolderGenerator
{
    /// <summary>The folder this generator writes.</summary>
    public const string Folder = "07_triggers";

    private const string Title = "Triggers";

    public async Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx)
    {
        var ct = ctx.Ct;
        var result = new FolderResult { Folder = Folder, Title = Title, InRunAll = false };

        var missing = ctx.Source.SortedModules()
            .Select(k => ctx.Source.Modules[k])
            .Where(m => string.Equals(m.OType, "TR", StringComparison.OrdinalIgnoreCase))
            .Where(m => !ctx.Target.Modules.ContainsKey(m.Name))
            .ToList();

        if (missing.Count == 0)
        {
            result.Notes.Add(ScriptGenerator.NothingToDoNote);
            result.Notes.Add($"{ctx.TargetEndpoint.Label} already has every trigger {ctx.SourceEndpoint.Label} has.");
            return [result];
        }

        // Probes. A failed probe returns null and leaves its own "not checked" note.
        var parents = await ctx.SourceProbes.TriggerParentsAsync(ct);
        var parentOf = new Dictionary<ObjectName, ObjectName>(ObjectName.IgnoreCase);
        foreach (var p in parents ?? []) parentOf[p.Trigger] = p.Table;
        var disabled = await ctx.SourceProbes.DisabledTriggersAsync(ct);
        var disabledSet = new HashSet<ObjectName>(disabled ?? [], ObjectName.IgnoreCase);
        var targetObjects = await ctx.Probes.ObjectsAsync(ct);
        var rowCounts = await ctx.Probes.RowCountsAsync(ct);

        var quarantined = new HashSet<ObjectName>(ctx.Facts.HeldQuarantine.Select(h => h.Name), ObjectName.IgnoreCase);
        var planned = new List<(ModuleRow Module, ObjectName Parent, long Rows, bool NewParent)>();

        foreach (var m in missing)
        {
            ct.ThrowIfCancellationRequested();
            var name = m.Name;

            // already reported by 03 in quarantined_not_applied.md
            if (quarantined.Contains(name)) continue;
            if (ctx.IsExcluded(name))
            {
                Hold(ctx, name, null, "matches an exclude pattern");
                continue;
            }

            // encrypted or CLR: there is no text to copy
            if (m.Definition is null)
            {
                Hold(ctx, name, null, ScriptText.RawModuleProblem(null)!);
                continue;
            }

            ObjectName? parent = parentOf.TryGetValue(name, out var pt) && pt.Name.Length > 0 ? pt : ParentFromDefinition(m);
            if (parent is not { } table)
            {
                Hold(ctx, name, null, "could not tell which table the trigger is on");
                continue;
            }
            var tableText = table.ToString();

            // The Python created a disabled trigger enabled. Without the probe we cannot tell (folder note below).
            if (disabledSet.Contains(name))
            {
                Hold(ctx, name, tableText,
                    $"disabled in {ctx.SourceEndpoint.Label} - CREATE TRIGGER would create it enabled and change what every write to the table does");
                continue;
            }

            var newParent = ParentCreatedBy03(ctx, table);
            if (!newParent && !ParentInTarget(ctx, table))
            {
                Hold(ctx, name, tableText, $"its table is not in {ctx.TargetEndpoint.Label} and 03_missing does not create it");
                continue;
            }

            var clash = targetObjects?.FirstOrDefault(o => o.Name.SameAs(name));
            if (clash is not null)
            {
                Hold(ctx, name, tableText,
                    $"the name is already used in {ctx.TargetEndpoint.Label} by an object of type {clash.Type} - CREATE would fail (Msg 2714)");
                continue;
            }

            var blocked = BlockedColumnsUsed(ctx, table, m.Definition!);
            if (blocked.Count > 0)
            {
                Hold(ctx, name, tableText,
                    $"uses column(s) 01_columns does not add ({string.Join(", ", blocked)}) - CREATE would fail (Msg 207)");
                continue;
            }

            var check = await ctx.SourceProbes.CompileCheckAsync(m, ct);
            if (check.Compiles == false)
            {
                Hold(ctx, name, tableText,
                    $"does not compile in {ctx.SourceEndpoint.Label} (Msg {check.ErrorNumber}: {check.Message}) - its CREATE fails anywhere");
                continue;
            }
            if (check.Note is not null)
                ctx.Review.Note(ReviewLog.NotApplied, "trigger", name.ToString(), tableText, $"created, but check it: {check.Note}", Folder);

            long rows = 0;
            if (!newParent && rowCounts is not null && rowCounts.TryGetValue(table, out var n)) rows = n;
            planned.Add((m, table, rows, newParent));
        }

        if (disabled is null)
            result.Notes.Add($"Could not read which {ctx.SourceEndpoint.Label} triggers are disabled; a disabled one would be created enabled - check before running.");

        // least-busy table first, so a run can stop before the hot one
        planned = planned
            .OrderBy(p => p.Rows)
            .ThenBy(p => p.Module.Obj.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(p => p.Module.Sch.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        if (planned.Count == 0)
        {
            result.Notes.Insert(0, ScriptGenerator.NothingToDoNote);
            result.Notes.Add("Every missing trigger was held back - see 99_review.");
            return [result];
        }

        // CREATE TRIGGER cannot carry a first/last order (sp_settriggerorder); say so instead of losing it silently.
        var ordered = await ctx.SourceProbes.OrderedTriggerNamesAsync(ct);
        if (ordered is null)
            result.Notes.Add($"Could not read which {ctx.SourceEndpoint.Label} triggers are set to fire first or last; that order is not carried by these scripts.");
        else
        {
            var orderedSet = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
            foreach (var (m, table, _, _) in planned.Where(p => orderedSet.Contains(p.Module.Obj)))
                ctx.Review.Note(ReviewLog.NotApplied, "trigger", m.Name.ToString(), table.ToString(),
                    $"created, but it is set to fire first or last in {ctx.SourceEndpoint.Label}; CREATE TRIGGER does not carry that - run sp_settriggerorder by hand after it", Folder);
        }

        var fwd = new StringBuilder();
        var back = new StringBuilder();
        var expected = new List<ExpectedModule>();
        var number = 0;
        foreach (var (m, table, _, _) in planned)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            var def = m.Definition!;
            var settings = ModuleSettings.Of(m);
            var file = $"{number:000}_{ScriptText.Safe(m.Obj)}.sql";
            var hdr = ScriptText.ModHdr(m);
            var block = hdr + ScriptText.ExecBlock(def) + "GO\r\n";

            // Raw per-object file = MODHDR + the definition, byte for byte (what sqlcmd sends is what is stored).
            // A NUL or an unpaired surrogate cannot travel that way; such a module goes through ExecBlock instead.
            if (ScriptText.RawModuleProblem(def) is null)
                ctx.Writer.WriteSql(Folder, $"objects\\{file}", hdr + def, WrittenFileKind.RawModule,
                    obj: m.Name, expectedModuleText: def, settings: settings);
            else
                ctx.Writer.WriteSql(Folder, $"objects\\{file}", block, WrittenFileKind.ExecBlockModules,
                    obj: m.Name, settings: settings, expectedModules: [new ExpectedModule(m.Name, def, settings)]);

            var drop = DropSql(m.Name);
            ctx.Writer.WriteSql(Folder, $"rollback\\{file}", drop, WrittenFileKind.Statements, obj: m.Name);

            fwd.Append(block);
            back.Append(drop);
            expected.Add(new ExpectedModule(m.Name, def, settings));
            ctx.Facts.Triggers.Add(m.Name);
        }

        ctx.Writer.WriteSql(Folder, "_ALL.sql", fwd.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: expected);
        ctx.Writer.WriteSql(Folder, "rollback\\_ALL.sql", back.ToString(), WrittenFileKind.Statements);
        ctx.Writer.WriteRunner(Folder, ctx.RunnerServer);

        result.Files = ctx.Writer.ObjectFileCount(Folder);
        result.HasRollback = true;
        var onNew = planned.Count(p => p.NewParent);
        result.Notes.Add("Run it after 01_columns and 03_missing: a trigger's CREATE fails if its table or a column it uses is not there yet.");
        if (onNew > 0)
            result.Notes.Add($"{onNew} of them {(onNew == 1 ? "is" : "are")} on tables 03_missing creates.");
        return [result];
    }

    /// <summary>The rollback: drop the trigger again if it is there (generate_triggers.py, verbatim for ordinary names).</summary>
    private static string DropSql(ObjectName name) =>
        $"IF OBJECT_ID({ScriptText.Lit(name.Bracketed)}, N'TR') IS NOT NULL\r\n    DROP TRIGGER {name.Bracketed}\r\nGO\r\n";

    private static void Hold(ScriptContext ctx, ObjectName trigger, string? table, string reason, string? section = null) =>
        ctx.Review.Hold(ReviewLog.NotApplied, "trigger", trigger.ToString(), table, reason, section ?? Folder);

    /// <summary>True when 03 creates the table or view (its manifest, or the missing-tables list when 03 was not selected).</summary>
    private static bool ParentCreatedBy03(ScriptContext ctx, ObjectName table) =>
        ctx.Facts.IsNewTable(table) ||
        ctx.Facts.MissingItems.Any(i => i.Object.SameAs(table) && i.Kind is "table" or "view");

    /// <summary>True when the target has the table, or a view of that name (INSTEAD OF triggers sit on views).</summary>
    private static bool ParentInTarget(ScriptContext ctx, ObjectName table) =>
        ctx.Target.Tables.Contains(table) ||
        ctx.Target.FindModule(table) is { } v && string.Equals(v.OType, "V", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Columns of the parent the target will not have after 01 (blocked, or on an excluded table) that the trigger
    /// names: SQL Server binds a trigger's columns of an existing table at CREATE, so it would fail there with Msg 207.
    /// </summary>
    private static List<string> BlockedColumnsUsed(ScriptContext ctx, ObjectName table, string definition)
    {
        var blocked = ctx.Facts.UnavailableColumns.Where(b => b.Table.SameAs(table)).Select(b => b.Column).ToList();
        if (blocked.Count == 0) return [];
        var words = new HashSet<string>(
            TsqlTokens.Tokens(definition).Where(t => t.Kind == 'I').Select(t => t.Text), StringComparer.OrdinalIgnoreCase);
        return blocked.Where(words.Contains).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Fallback when the source's sys.triggers could not be read: the <c>ON [schema].[table]</c> right after
    /// the trigger name in its CREATE TRIGGER statement. An unqualified table is taken in the trigger's schema
    /// (a DML trigger always lives in its table's schema).
    /// </summary>
    private static ObjectName? ParentFromDefinition(ModuleRow m)
    {
        var mt = CreateTriggerOnRx().Match(TsqlTokens.Strip(m.Definition ?? ""));
        if (!mt.Success) return null;
        static string Part(Group g) =>
            g.Value.StartsWith('[') ? g.Value[1..^1].Replace("]]", "]")
            : g.Value.StartsWith('"') ? g.Value[1..^1]
            : g.Value;
        var second = mt.Groups["b"];
        return second.Success
            ? new ObjectName(Part(mt.Groups["a"]), Part(second))
            : new ObjectName(m.Sch, Part(mt.Groups["a"]));
    }

    private const string NamePart = @"(?:\[(?:[^\]]|\]\])+\]|""[^""]+""|[A-Za-z_@#][\w@#$]*)";

    [GeneratedRegex(@"^\s*(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+TRIGGER\s+" + NamePart + @"(?:\s*\.\s*" + NamePart + @")?\s+ON\s+(?<a>" + NamePart + @")(?:\s*\.\s*(?<b>" + NamePart + "))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTriggerOnRx();
}
