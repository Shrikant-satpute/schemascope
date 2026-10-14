using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;

namespace SchemaScope.Api.Services;

/// <summary>
/// "Generate script" for one object of a finished compare: what it would take (<see cref="Plan"/>), what else it
/// needs that the target lacks (<see cref="Dependencies"/>), and the run itself (<see cref="RunAsync"/>) - the
/// ordinary <see cref="ScriptGenerator"/> with <see cref="ScriptOptions.OnlyObjects"/> set, never a second engine.
/// The dependency check works from the compare's snapshots in memory and touches no database.
/// </summary>
public static partial class ObjectScripts
{
    /// <summary>The most dependencies one request may name (and the dependency check lists).</summary>
    public const int MaxDependencies = 50;

    /// <summary>Where object scripts are written: a fixed scratch folder, pruned at start.</summary>
    public static string ScratchRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchemaScope", "object-scripts");

    /// <summary>How long a run folder under <see cref="ScratchRoot"/> is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public const string DropWarning = "SchemaScope never runs this; dropping can break apps and lose data.";

    /// <summary>The order an object script's folders run in; its text is assembled in this order.</summary>
    internal static readonly string[] RunOrder =
    [
        CoreFoldersGenerator.ColumnTypesFolder, CoreFoldersGenerator.ColumnsFolder, CoreFoldersGenerator.TableTypesFolder,
        CoreFoldersGenerator.MissingFolder, CoreFoldersGenerator.ConstraintsFolder, CoreFoldersGenerator.IndexesFolder,
        TriggersGenerator.Folder, FormattingGenerator.ModulesFolder, FormattingGenerator.TablesFolder, DifferentModulesGenerator.Folder,
    ];

    /// <summary>Objects the generator creates before anything that uses them, without being asked (schemas, alias types, sequences, synonyms).</summary>
    private static bool IsPrerequisite(DbObjectType t) =>
        t is DbObjectType.Schema or DbObjectType.UserDefinedType or DbObjectType.Sequence or DbObjectType.Synonym;

    private static ObjectStatus StatusOf(CompareRow row, int index) =>
        index < row.Cells.Count ? row.Cells[index].Status : ObjectStatus.Unavailable;

    // ------------------------------------------------------------------ checks

    /// <summary>The 400 reasons shared by both routes, or null when the run, target and key are usable.</summary>
    public static string? Check(CompareSession session, string? key, int? targetIndex, out CompareRow? row)
    {
        row = null;
        if (session.Status != "done" || session.Result is null)
            return session.Status == "running"
                ? "That comparison is still running - wait for it to finish."
                : "That comparison did not finish - run the compare again.";
        if (targetIndex is not { } index)
            return "Say which target to script against.";
        if (index < 0 || index >= session.Result.Targets.Count)
            return $"This comparison has no target number {index + 1}.";
        if (string.IsNullOrEmpty(key) || !session.RowIndex.TryGetValue(key, out row))
            return "There is no object with that key in this comparison.";
        return null;
    }

    /// <summary>
    /// The dependencies a request names, checked: at most <see cref="MaxDependencies"/>, each a row of this
    /// comparison that is missing or different in the target (anything else has nothing to script).
    /// </summary>
    public static string? CheckIncludes(CompareSession session, CompareRow row, int index, IReadOnlyList<string>? keys,
        out List<CompareRow> included)
    {
        included = [];
        var wanted = (keys ?? []).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count > MaxDependencies) return $"Pick at most {MaxDependencies} dependencies.";
        var label = session.Result!.Targets[index].Label;
        foreach (var k in wanted)
        {
            if (k == row.Key) continue;
            if (!session.RowIndex.TryGetValue(k, out var dep)) return "A dependency is not an object of this comparison.";
            if (StatusOf(dep, index) is not (ObjectStatus.MissingInTarget or ObjectStatus.Different))
                return $"{dep.FullName} is not missing or different in {label}, so there is nothing to script for it.";
            included.Add(dep);
        }
        return null;
    }

    // ------------------------------------------------------------------ plan

    /// <summary>What a script for <paramref name="row"/> against target <paramref name="index"/> does; <paramref name="why"/> explains identical / unavailable.</summary>
    public static string Plan(CompareResult result, CompareRow row, int index, out string? why)
    {
        why = null;
        var target = result.Targets[index];
        if (target.Failed)
        {
            why = $"{target.Label} could not be read in this comparison, so there is nothing to script against. Fix the connection and run the compare again.";
            return "unavailable";
        }
        switch (StatusOf(row, index))
        {
            case ObjectStatus.Same:
                why = $"Already the same in {target.Label}.";
                return "identical";
            case ObjectStatus.OnlyInTarget:
                return "extra";
            case ObjectStatus.Unavailable:
                why = $"Its text could not be compared ({row.Cells[index].Note ?? "encrypted, or no VIEW DEFINITION"}), so it cannot be scripted.";
                return "unavailable";
            case ObjectStatus.MissingInTarget:
                return row.Type == DbObjectType.Trigger ? "trigger" : "create";
        }
        var formatting = StatusOf(row, index) == ObjectStatus.FormattingOnly;
        switch (row.Type)
        {
            case DbObjectType.Table: return "tableChanges";
            case DbObjectType.TableType: return "tableType";
            case DbObjectType.View or DbObjectType.StoredProcedure or DbObjectType.ScalarFunction
                or DbObjectType.TableValuedFunction or DbObjectType.Trigger:
                return formatting ? "formatting" : "alter";
            default:
                why = $"SchemaScope does not script changes to an existing {row.TypeLabel.ToLowerInvariant()}.";
                return "unavailable";
        }
    }

    // ------------------------------------------------------------------ dependencies

    /// <summary>The plan, its warnings, and the in-comparison objects it needs that the target lacks or has differently.</summary>
    public static ObjectDependencies Dependencies(CompareSession session, CompareRow row, int index)
    {
        var result = session.Result!;
        var plan = Plan(result, row, index, out var why);
        var deps = new ObjectDependencies { Key = row.Key, Status = StatusOf(row, index), Plan = plan };
        if (why is not null) deps.Warnings.Add(why);
        // the risk of a table-type rebuild, a trigger or an overwrite comes with the plan: the dialog states it once
        if (plan == "extra") deps.Warnings.Add(DropWarning);
        if (plan is "identical" or "unavailable" or "extra" || !session.SourceIndex.TryGetValue(row.Key, out var subject))
            return deps;

        var candidates = Candidates(session, index);
        var found = new Dictionary<string, ObjectDependency>(StringComparer.Ordinal);
        var queue = new Queue<DbObject>([subject]);
        var queued = new HashSet<string>(StringComparer.Ordinal) { subject.Key };
        while (queue.Count > 0 && found.Count < MaxDependencies)
        {
            var obj = queue.Dequeue();
            var direct = ReferenceEquals(obj, subject);
            foreach (var (dep, required, reason) in DirectNeeds(session, obj, index, candidates))
            {
                if (dep.Key == subject.Key || found.ContainsKey(dep.Key)) continue;
                if (!direct && !required) continue;   // of a dependency, only what it cannot be created without
                found[dep.Key] = new ObjectDependency
                {
                    Key = dep.Key,
                    DisplayName = dep.FullName,
                    Type = dep.TypeLabel,
                    Status = StatusOf(dep, index),
                    Required = required && IsPrerequisite(dep.Type),   // the server adds these itself
                    DefaultIncluded = required,
                    Reason = direct ? reason : $"{obj.FullName} needs it: {reason}",
                };
                if (required && queued.Add(dep.Key) && session.SourceIndex.TryGetValue(dep.Key, out var next)) queue.Enqueue(next);
                if (found.Count >= MaxDependencies) break;
            }
        }
        // what is left unticked is the dialog's to warn about, as the ticks change; the run's answer names it again
        deps.Warnings.AddRange(RuntimeWarnings(session, row, subject, index, []));
        deps.Dependencies = [.. found.Values.OrderByDescending(d => d.Required).ThenByDescending(d => d.DefaultIncluded).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)];
        return deps;
    }

    /// <summary>
    /// What makes the object fail when it runs although its CREATE works: names neither database has, and the
    /// missing objects in <paramref name="deferred"/> that are not scripted with it.
    /// </summary>
    private static List<string> RuntimeWarnings(CompareSession session, CompareRow row, DbObject subject, int index, List<string> deferred)
    {
        var label = session.Result!.Targets[index].Label;
        var warnings = new List<string>();
        var unknown = UnknownNames(session, subject, index);
        if (unknown.Count > 0)
            warnings.Add($"{row.FullName} names {string.Join(", ", unknown)}, which neither {session.Result.Source.Label} nor {label} has, " +
                         "so nothing can script them - the statements that use them fail when they run, whatever is scripted here.");
        if (deferred.Count > 0)
            warnings.Add($"{row.FullName} is created without {string.Join(", ", deferred)} ({label} lacks {(deferred.Count == 1 ? "it" : "them")}), " +
                         "but it fails when it runs until they exist - SQL Server resolves those names only at run time.");
        return warnings;
    }

    /// <summary>Rows that are missing or different in the target, by lower-cased name (triggers and schemas are never named by others).</summary>
    private static Dictionary<string, List<CompareRow>> Candidates(CompareSession session, int index)
    {
        var d = new Dictionary<string, List<CompareRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in session.Result!.Rows)
        {
            if (r.Type is DbObjectType.Trigger or DbObjectType.Schema) continue;
            if (StatusOf(r, index) is not (ObjectStatus.MissingInTarget or ObjectStatus.Different)) continue;
            if (!d.TryGetValue(r.Name, out var l)) d[r.Name] = l = [];
            l.Add(r);
        }
        return d;
    }

    /// <summary>
    /// What <paramref name="obj"/> names (by word, as the "uses" search does) that the target lacks or has differently,
    /// and whether its CREATE fails without it: prerequisites and table types always; a missing object when
    /// <paramref name="obj"/> binds its names at CREATE (a table, view, inline function, schema-bound module, a
    /// trigger's table); a changed table when <paramref name="obj"/> names a column that table lacks in the target.
    /// A missing object a procedure or a scalar / multi-statement function reads is listed but not required.
    /// </summary>
    private static IEnumerable<(CompareRow Row, bool Required, string Reason)> DirectNeeds(
        CompareSession session, DbObject obj, int index, Dictionary<string, List<CompareRow>> candidates)
    {
        var label = session.Result!.Targets[index].Label;
        var schemaKey = new DbObject { Type = DbObjectType.Schema, Schema = obj.Schema, Name = obj.Schema }.Key;
        if (obj.Type != DbObjectType.Schema && session.RowIndex.TryGetValue(schemaKey, out var schema) &&
            StatusOf(schema, index) == ObjectStatus.MissingInTarget)
            yield return (schema, true, $"its schema - {label} does not have it, so it is created first");

        var text = TsqlTokens.FlowNorm(TextOf(obj));   // comments and string literals out: a name there is not a use
        var words = Words(text);
        var binds = obj.Type is DbObjectType.Table or DbObjectType.TableType or DbObjectType.View ||
                    (obj.Type == DbObjectType.TableValuedFunction && InlineRx().IsMatch(text)) ||
                    words.Contains("SCHEMABINDING");
        foreach (var word in words)
        {
            if (!candidates.TryGetValue(word, out var rows)) continue;
            foreach (var dep in rows)
            {
                if (dep.Key == obj.Key) continue;
                var status = StatusOf(dep, index);
                var type = dep.TypeLabel.ToLowerInvariant();
                if (IsPrerequisite(dep.Type))
                {
                    if (status == ObjectStatus.MissingInTarget)
                        yield return (dep, true, $"a {type} {label} lacks - created first, automatically");
                    continue;
                }
                if (dep.Type == DbObjectType.TableType)
                {
                    yield return status == ObjectStatus.MissingInTarget
                        ? (dep, true, $"it takes this table type, which {label} lacks - CREATE fails without it (Msg 2715)")
                        : (dep, false, $"it takes this table type, whose columns differ in {label}; rebuilding it drops and recreates every module " +
                                       "that uses it and breaks the deployed application's calls - include it only with the new application release");
                    continue;
                }
                if (status == ObjectStatus.MissingInTarget)
                {
                    var parent = obj.Type == DbObjectType.Trigger && dep.Type == DbObjectType.Table &&
                                 string.Equals(obj.ParentName, dep.FullName, StringComparison.OrdinalIgnoreCase);
                    if (parent)
                        yield return (dep, true, $"the trigger's table - {label} lacks it, so CREATE TRIGGER fails without it");
                    else if (binds)
                        yield return (dep, true, obj.Type is DbObjectType.Table or DbObjectType.TableType
                            ? $"a foreign key, default or computed column uses it and {label} lacks it - the table cannot be created as it is without it"
                            : $"a {obj.Type.Label().ToLowerInvariant()} that binds every name when it is created reads it, and {label} lacks it - CREATE fails without it (Msg 208)");
                    else
                        yield return (dep, false, $"{label} lacks it; CREATE works without it (deferred name resolution), but the code fails when it runs until it exists");
                    continue;
                }
                if (dep.Type == DbObjectType.Table && NewColumnNamed(session, dep, index, words) is { } col)
                    yield return (dep, true, $"it names {col}, a column {label}'s table does not have yet - CREATE fails without it (Msg 207)");
            }
        }
    }

    /// <summary>The text whose words are what <paramref name="obj"/> uses: a module's code; a table's column types, defaults, computed columns, checks and foreign keys.</summary>
    private static string TextOf(DbObject obj)
    {
        if (obj.Type.IsModule()) return obj.Definition ?? "";
        var sb = new StringBuilder();
        foreach (var c in obj.Columns)
            sb.Append(c.DataType).Append('\n').Append(c.DefaultDefinition).Append('\n').Append(c.ComputedDefinition).Append('\n');
        foreach (var c in obj.Constraints) sb.Append(c.Definition).Append('\n');
        foreach (var f in obj.ForeignKeys)
            sb.Append(f.ReferencedSchema).Append('.').Append(f.ReferencedTable).Append(' ').AppendJoin(' ', f.ReferencedColumns).Append('\n');
        return sb.ToString();
    }

    /// <summary>A column the source's table has, the target's does not, and <paramref name="words"/> names - or null.</summary>
    private static string? NewColumnNamed(CompareSession session, CompareRow table, int index, HashSet<string> words)
    {
        if (!session.SourceIndex.TryGetValue(table.Key, out var s) || index >= session.TargetIndexes.Count ||
            !session.TargetIndexes[index].TryGetValue(table.Key, out var t))
            return null;
        var have = new HashSet<string>(t.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return s.Columns.Select(c => c.Name).FirstOrDefault(c => !have.Contains(c) && words.Contains(c));
    }

    /// <summary>
    /// Tables and procedures a module reads, writes or runs (the name after FROM, JOIN, INTO, EXEC) that neither
    /// database has. A rough read of the text, so it only warns: temp tables, variables, CTEs, cursors, function
    /// calls, system and other-database names are left out.
    /// </summary>
    private static List<string> UnknownNames(CompareSession session, DbObject obj, int index)
    {
        if (!obj.Type.IsModule() || obj.Definition is null) return [];
        var text = TsqlTokens.FlowNorm(obj.Definition);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in session.Source!.Objects) known.Add(o.Name);
        if (index < session.Targets.Count) foreach (var o in session.Targets[index].Objects) known.Add(o.Name);
        var local = new HashSet<string>(LocalNameRx().Matches(text).Select(m => m.Groups["local"].Value), StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in UsedNameRx().Matches(text))
        {
            if (m.Groups["call"].Success) continue;   // OPENJSON(...), STRING_SPLIT(...), EXEC(@sql)
            var parts = m.Groups["name"].Value.Split('.').Select(p => p.Trim()).ToArray();
            if (parts.Length > 2 || (parts.Length == 2 && parts[0] is "sys" or "information_schema")) continue;
            var name = parts[^1];
            if (name.Length < 3 || name[0] is '@' or '#' || name is "inserted" or "deleted" ||
                name.StartsWith("sp_", StringComparison.Ordinal) || name.StartsWith("xp_", StringComparison.Ordinal) ||
                TsqlTokens.Keywords.Contains(name) || local.Contains(name) || known.Contains(name))
                continue;
            var at = obj.Definition.IndexOf(name, StringComparison.OrdinalIgnoreCase);   // FlowNorm lower-cased it: show the module's spelling
            unknown.Add(at >= 0 ? obj.Definition.Substring(at, name.Length) : name);
        }
        return [.. unknown];
    }

    /// <summary>On <see cref="TsqlTokens.FlowNorm"/> text: what follows FROM / JOIN / INTO / EXEC, and whether a "(" follows it.</summary>
    /// A name followed by a further "." (<c>master..spt_values</c>) is another database's and does not match.
    [GeneratedRegex(@"\b(?:from|join|into|exec|execute) (?<name>(?>[\w@#$]+(?: ?\. ?[\w@#$]+)*))(?! ?\.)(?<call> ?\()?", RegexOptions.CultureInvariant)]
    private static partial Regex UsedNameRx();

    /// <summary>
    /// On <see cref="TsqlTokens.FlowNorm"/> text: names the module defines itself - CTEs (<c>x as (</c>), cursors,
    /// and table aliases (<c>from t x</c>, <c>join t as x</c>; <c>delete from x</c> names the alias).
    /// </summary>
    [GeneratedRegex(@"\b(?<local>[\w$]+) ?(?:\([^)]*\) ?)?as ?\(|(?:\bdeclare|,) ?(?<local>[\w$]+) (?:insensitive |scroll )?cursor\b|\b(?:from|join) (?=[\w@#$.]+ (?:as )?(?<local>[\w$]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocalNameRx();

    private static HashSet<string> Words(string text) =>
        new(WordRx().Matches(text).Select(m => m.Value), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}\p{N}_@#$]+")]
    private static partial Regex WordRx();

    /// <summary>An inline table-valued function: RETURNS TABLE (a multi-statement one says RETURNS @t TABLE).</summary>
    [GeneratedRegex(@"\bRETURNS\s+TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineRx();

    // ------------------------------------------------------------------ extra

    /// <summary>The DROP for an object only the target has, names bracket-quoted as QUOTENAME does. Copy-only.</summary>
    public static string DropPreview(CompareRow row, string targetLabel)
    {
        var name = row.Type == DbObjectType.Schema ? Ddl.Bracket(row.Name) : Ddl.Bracket(row.Schema, row.Name);
        var word = row.Type switch
        {
            DbObjectType.Table => "TABLE",
            DbObjectType.View => "VIEW",
            DbObjectType.StoredProcedure => "PROCEDURE",
            DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction => "FUNCTION",
            DbObjectType.AggregateFunction => "AGGREGATE",
            DbObjectType.Trigger => "TRIGGER",
            DbObjectType.UserDefinedType or DbObjectType.TableType => "TYPE",
            DbObjectType.Sequence => "SEQUENCE",
            DbObjectType.Synonym => "SYNONYM",
            _ => "SCHEMA",
        };
        return $"-- {DropWarning}\r\n-- {row.TypeLabel} {row.FullName.ReplaceLineEndings(" ")} exists only in {targetLabel.ReplaceLineEndings(" ")}.\r\nDROP {word} {name};\r\nGO\r\n";
    }

    /// <summary>The answer for an object only the target has: nothing generated, nothing written, the DROP to copy.</summary>
    public static ObjectScriptOutcome ExtraOutcome(CompareRow row, string targetLabel) => new()
    {
        Success = true,
        Message = $"{row.FullName} exists only in {targetLabel}. SchemaScope generates nothing for it; the DROP statement is there to copy.",
        DropPreview = DropPreview(row, targetLabel),
        Warnings = [DropWarning],
    };

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Runs the generator for <paramref name="row"/>, the chosen dependencies and the prerequisites they all need,
    /// into a new folder under <paramref name="root"/>, and assembles the parts it wrote in run order.
    /// </summary>
    public static async Task<ObjectScriptOutcome> RunAsync(
        CompareSession session, CompareRow row, int index, List<CompareRow> included,
        ScriptEndpoint source, ScriptEndpoint target,
        IReadOnlySet<string> formattingCandidates, IReadOnlyDictionary<string, LineChange> differentCandidates,
        ScriptGenerator generator, string root, IProgress<ScriptProgress>? progress, CancellationToken ct)
    {
        // prerequisites of everything in the script, whether the request named them or not
        var scope = new List<CompareRow> { row };
        scope.AddRange(included);
        var candidates = Candidates(session, index);
        foreach (var r in scope.ToList())
            if (session.SourceIndex.TryGetValue(r.Key, out var obj))
                foreach (var (dep, required, _) in DirectNeeds(session, obj, index, candidates))
                    if (required && IsPrerequisite(dep.Type) && scope.All(s => s.Key != dep.Key)) scope.Add(dep);

        var statuses = scope.Select(r => (r.Type, Status: StatusOf(r, index))).ToList();
        var sections = new List<ScriptSection>
        {
            ScriptSection.NewColumns, ScriptSection.MissingObjects, ScriptSection.ColumnTypes, ScriptSection.Constraints, ScriptSection.Indexes,
        };
        if (statuses.Any(s => s.Type == DbObjectType.TableType && s.Status == ObjectStatus.Different)) sections.Add(ScriptSection.TableTypes);
        if (statuses.Any(s => s.Type == DbObjectType.Trigger && s.Status == ObjectStatus.MissingInTarget)) sections.Add(ScriptSection.Triggers);
        if (statuses.Any(s => s.Type.IsModule() && s.Status == ObjectStatus.FormattingOnly)) sections.Add(ScriptSection.Formatting);
        if (statuses.Any(s => s.Type.IsModule() && s.Status == ObjectStatus.Different)) sections.Add(ScriptSection.DifferentModules);

        static string Short(string s) => s.Length > 40 ? s[..40] : s;
        var options = new ScriptOptions
        {
            Sections = sections,
            OutputFolder = root,
            RunFolderName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Short(target.Label)}_{Short(row.FullName)}",
            OnlyObjects = new HashSet<ObjectName>(
                scope.Select(r => r.Type == DbObjectType.Schema ? new ObjectName(r.Name, "") : new ObjectName(r.Schema, r.Name)),
                ObjectName.IgnoreCase),
        };

        var outcome = await generator.GenerateAsync(source, target, options, formattingCandidates, ct, progress, differentCandidates);
        var result = Assemble(outcome, row, target.Label, [.. scope.Skip(1).Select(r => r.Key)]);
        if (session.SourceIndex.TryGetValue(row.Key, out var subject))
        {
            var deferred = DirectNeeds(session, subject, index, candidates)
                .Where(d => !d.Required && StatusOf(d.Row, index) == ObjectStatus.MissingInTarget && scope.All(s => s.Key != d.Row.Key))
                .Select(d => d.Row.FullName).Distinct().ToList();
            result.Warnings.AddRange(RuntimeWarnings(session, row, subject, index, deferred));
        }
        return result;
    }

    /// <summary>The API answer from the generator's outcome: the objects\ files in run order, their text, the rollback, the holds.</summary>
    internal static ObjectScriptOutcome Assemble(ScriptOutcome outcome, CompareRow row, string targetLabel, List<string> dependencies)
    {
        var result = new ObjectScriptOutcome
        {
            Success = outcome.Success,
            Message = outcome.Message ?? "",
            HeldBack = [.. outcome.HeldBack.Select(h => new ObjectScriptHeld { ObjectName = h.Object, Reason = h.Reason })],
            Warnings = outcome.Warnings,
            DependenciesIncluded = dependencies,
            ElapsedMs = outcome.ElapsedMs,
            Path = outcome.Path ?? "",
        };
        var v = outcome.Validation;
        result.Validation = new ObjectScriptValidation
        {
            Passed = v.Passed,
            Summary = $"{(v.Passed ? "Passed" : "Problems found")}: {v.Batches} batches parsed offline, {v.SyntaxErrors} syntax errors; " +
                      $"{v.ModulesExact} of {v.ModulesChecked} module texts rebuild byte for byte." +
                      (v.Problems.Count > 0 ? " " + string.Join(" ", v.Problems.Take(3)) : ""),
        };
        if (!outcome.Success) return result;   // a stopped run's folder is marked incomplete: nothing to show or run

        int Order(WrittenFile f) => Array.FindIndex(RunOrder, r => string.Equals(r, f.Folder, StringComparison.OrdinalIgnoreCase));
        static bool Sql(WrittenFile f) => f.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
        static string Name(WrittenFile f) => f.Object?.ToString() ?? Path.GetFileNameWithoutExtension(f.RelativePath);
        static string OneLine(string s) => s.ReplaceLineEndings(" ");   // a name with a line break would end the comment it is in

        var script = new StringBuilder();
        foreach (var f in outcome.Files.Where(f => f.IsObjectFile && Sql(f) && Order(f) >= 0)
                     .OrderBy(Order).ThenBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            result.Parts.Add(new ObjectScriptPart
            {
                Folder = f.Folder,
                File = f.FullPath,
                ObjectName = Name(f),
                Kind = JsonNamingPolicy.CamelCase.ConvertName(f.Kind.ToString()),
            });
            script.Append($"-- {f.Folder}: {OneLine(Name(f))}\r\n").Append(WithGo(ScriptWriter.ReadBack(f.FullPath)));
        }
        result.Script = script.ToString();

        // undo runs the other way round: the last folder's rollback first
        var rollback = new StringBuilder();
        foreach (var f in outcome.Files.Where(f => f.IsRollback && Sql(f) && Order(f) >= 0 &&
                                                   !f.RelativePath.EndsWith("_ALL.sql", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(Order).ThenBy(f => f.RelativePath, StringComparer.Ordinal))
            rollback.Append($"-- {f.Folder} rollback: {OneLine(Name(f))}\r\n").Append(WithGo(ScriptWriter.ReadBack(f.FullPath)));
        result.Rollback = rollback.Length > 0 ? rollback.ToString() : null;

        var held = result.HeldBack.Count;
        result.Message = result.Parts.Count > 0
            ? $"Scripted {row.FullName} for {targetLabel}: {result.Parts.Count} file(s)" +
              (held > 0 ? $"; {held} item(s) held back - see why below." : ".")
            : held > 0
                ? $"Nothing was scripted for {row.FullName}: {held} item(s) held back - see why below."
                : $"Nothing was scripted for {row.FullName}: the generator found nothing it can change for it in {targetLabel}.";
        return result;
    }

    /// <summary>
    /// A part as it can be pasted after another: a raw module file ends with the module's last line and no GO
    /// (sqlcmd runs the last batch at end of file), so the next part would join its batch.
    /// </summary>
    private static string WithGo(string text)
    {
        var last = text.TrimEnd().Split('\n')[^1].Trim();
        if (string.Equals(last, "GO", StringComparison.OrdinalIgnoreCase)) return text;
        return text + (text.EndsWith('\n') ? "" : "\r\n") + "GO\r\n";
    }

    // ------------------------------------------------------------------ scratch folder

    /// <summary>
    /// Deletes run folders directly under <paramref name="root"/> created more than <paramref name="maxAge"/> ago.
    /// Nothing outside <paramref name="root"/> is touched: only its own subfolders, never a junction or link.
    /// </summary>
    public static void Prune(string root, DateTime nowUtc, TimeSpan maxAge)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var dir = new DirectoryInfo(full);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var run in dir.EnumerateDirectories())
            {
                if (run.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!string.Equals(Path.GetDirectoryName(run.FullName), full, StringComparison.OrdinalIgnoreCase)) continue;
                if (nowUtc - run.CreationTimeUtc < maxAge) continue;
                try { run.Delete(recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next start */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // housekeeping only
        }
    }
}
