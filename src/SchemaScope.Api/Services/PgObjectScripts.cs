using System.Text;
using System.Text.Json;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Api.Services;

/// <summary>
/// "Generate script" for one PostgreSQL object (R2b): the ordinary <see cref="PgScriptGenerator"/>, limited by
/// <see cref="ScriptOptions.OnlyKeys"/> to the object, the dependencies picked with it and the schemas they need. What it
/// needs comes from pg_depend (<see cref="DbObject.DependsOn"/>) in the compare's snapshots; no database is touched to
/// find out. The SQL Server path (<see cref="ObjectScripts"/>) is not used for PostgreSQL at all.
/// </summary>
public static class PgObjectScripts
{
    /// <summary>The order the parts run in; the script is assembled in this order.</summary>
    private static readonly string[] RunOrder =
    [
        PgScriptGenerator.ColumnTypesFolder, PgScriptGenerator.ColumnsFolder, PgScriptGenerator.MissingFolder,
        PgScriptGenerator.ConstraintsFolder, PgScriptGenerator.IndexesFolder, PgScriptGenerator.TriggersFolder,
        PgScriptGenerator.FormattingModulesFolder, PgScriptGenerator.FormattingTablesFolder, PgScriptGenerator.DifferentFolder,
    ];

    /// <summary>The warning with a DROP to copy: Generate script never runs it; Create scripts can put it in 11_destructive.</summary>
    public const string DropWarning = ObjectScripts.DropWarning;

    private static ObjectStatus StatusOf(CompareRow row, int index) =>
        index < row.Cells.Count ? row.Cells[index].Status : ObjectStatus.Unavailable;

    /// <summary>
    /// What a script for the row does (as <see cref="ObjectScripts.Plan"/>), with PostgreSQL's own answers where they
    /// differ. Code that differs only in layout is re-created from the source's text (08), as Create scripts does. An
    /// existing aggregate, sequence, domain, enum, composite type, schema or materialized view is changed the way Create
    /// scripts changes it (10, 05, 04 or 06), or held there with why.
    /// </summary>
    public static string Plan(CompareResult result, CompareRow row, int index, out string? why)
    {
        var plan = ObjectScripts.Plan(result, row, index, out why);
        switch (plan)
        {
            case "unavailable" when StatusOf(row, index) == ObjectStatus.Different && row.Type is DbObjectType.AggregateFunction
                or DbObjectType.Sequence or DbObjectType.Domain or DbObjectType.EnumType or DbObjectType.CompositeType
                or DbObjectType.Schema or DbObjectType.MaterializedView:
                why = null;
                return "alter";
            default:
                return plan;
        }
    }

    /// <summary>The plan, its warnings, and what the object needs that the target lacks (from pg_depend).</summary>
    public static ObjectDependencies Dependencies(CompareSession session, CompareRow row, int index)
    {
        var result = session.Result!;
        var plan = Plan(result, row, index, out var why);
        var deps = new ObjectDependencies { Key = row.Key, Status = StatusOf(row, index), Plan = plan };
        if (why is not null) deps.Warnings.Add(why);
        if (plan == "extra") deps.Warnings.Add(DropWarning);
        if (plan is "identical" or "unavailable" or "extra" || !session.SourceIndex.TryGetValue(row.Key, out var subject)) return deps;

        var found = new Dictionary<string, ObjectDependency>(StringComparer.Ordinal);
        var queue = new Queue<DbObject>([subject]);
        var queued = new HashSet<string>(StringComparer.Ordinal) { subject.Key };
        while (queue.Count > 0 && found.Count < ObjectScripts.MaxDependencies)
        {
            var obj = queue.Dequeue();
            var direct = ReferenceEquals(obj, subject);
            foreach (var (dep, required, reason) in Needs(session, obj, index))
            {
                if (dep.Key == subject.Key || found.ContainsKey(dep.Key)) continue;
                if (!direct && !required) continue;
                found[dep.Key] = new ObjectDependency
                {
                    Key = dep.Key,
                    DisplayName = dep.Signature is { } sig && dep.Type != DbObjectType.Trigger ? $"{dep.FullName}({sig})" : dep.FullName,
                    Type = dep.TypeLabel,
                    Status = StatusOf(dep, index),
                    Required = required && dep.Type == DbObjectType.Schema,
                    DefaultIncluded = required,
                    Reason = direct ? reason : $"{obj.FullName} needs it: {reason}",
                };
                if (required && queued.Add(dep.Key) && session.SourceIndex.TryGetValue(dep.Key, out var next)) queue.Enqueue(next);
                if (found.Count >= ObjectScripts.MaxDependencies) break;
            }
        }
        deps.Dependencies = [.. found.Values.OrderByDescending(d => d.Required).ThenByDescending(d => d.DefaultIncluded)
            .ThenBy(d => d.DisplayName, StringComparer.Ordinal)];
        return deps;
    }

    /// <summary>What <paramref name="obj"/> needs that the target lacks: its schema, and every pg_depend need that is missing or a table missing a column it names.</summary>
    private static IEnumerable<(CompareRow Row, bool Required, string Reason)> Needs(CompareSession session, DbObject obj, int index)
    {
        var label = session.Result!.Targets[index].Label;
        var schemaKey = new DbObject { Type = DbObjectType.Schema, Schema = obj.Schema, Name = obj.Schema, Fold = Core.Engines.IdentifierFold.Exact }.Key;
        if (obj.Type != DbObjectType.Schema && session.RowIndex.TryGetValue(schemaKey, out var schema) && StatusOf(schema, index) == ObjectStatus.MissingInTarget)
            yield return (schema, true, $"its schema - {label} does not have it, so it is created first");

        var target = index < session.TargetIndexes.Count ? session.TargetIndexes[index] : [];
        foreach (var d in obj.DependsOn ?? [])
        {
            if (d.Key == obj.Key || !session.RowIndex.TryGetValue(d.Key, out var dep)) continue;
            var status = StatusOf(dep, index);
            if (status == ObjectStatus.MissingInTarget)
            {
                if (d.Via == "foreignkey")
                    yield return (dep, false, $"a foreign key references it and {label} lacks it - without it that key is held back");
                else
                    yield return (dep, true, $"{label} lacks it, and creating {obj.FullName} fails without it");
            }
            else if (status == ObjectStatus.Different && dep.Type == DbObjectType.Table && d.Column is { } col &&
                     target.TryGetValue(dep.Key, out var t) && t.Columns.All(c => c.Name != col))
                yield return (dep, true, $"it names {col}, a column {label}'s table does not have yet");
        }
    }

    /// <summary>The answer for an object only the target has: nothing written, the DROP to copy.</summary>
    public static ObjectScriptOutcome ExtraOutcome(CompareRow row, string targetLabel) => new()
    {
        Success = true,
        Message = $"{row.FullName} exists only in {targetLabel}. SchemaScope generates nothing for it; the DROP statement is there to copy.",
        DropPreview = DropPreview(row, targetLabel),
        Warnings = [DropWarning],
    };

    /// <summary>The DROP for an object only the target has, in PostgreSQL's spelling. Copy-only; nothing runs it.</summary>
    public static string DropPreview(CompareRow row, string targetLabel)
    {
        var name = row.Type == DbObjectType.Schema ? PgDdl.Q(row.Name) : PgDdl.Q(row.Schema, row.Name);
        var statement = row.Type switch
        {
            DbObjectType.Table => $"DROP TABLE {name};",
            DbObjectType.View => $"DROP VIEW {name};",
            DbObjectType.MaterializedView => $"DROP MATERIALIZED VIEW {name};",
            DbObjectType.StoredProcedure => $"DROP PROCEDURE {name}({row.Signature});",
            DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction => $"DROP FUNCTION {name}({row.Signature});",
            DbObjectType.AggregateFunction => $"DROP AGGREGATE {name}({row.Signature});",
            DbObjectType.Trigger => $"DROP TRIGGER {PgDdl.Q(row.Name)} ON {PgDdl.Q(row.Schema, row.ParentName ?? "")};",
            DbObjectType.Sequence => $"DROP SEQUENCE {name};",
            DbObjectType.Domain => $"DROP DOMAIN {name};",
            DbObjectType.EnumType or DbObjectType.CompositeType or DbObjectType.RangeType => $"DROP TYPE {name};",
            DbObjectType.Collation => $"DROP COLLATION {name};",
            DbObjectType.Operator => $"DROP OPERATOR {PgDdl.Q(row.Schema)}.{row.Name} ({row.Signature});",
            DbObjectType.Extension => $"DROP EXTENSION {PgDdl.Q(row.Name)};",
            DbObjectType.ForeignTable => $"DROP FOREIGN TABLE {name};",
            DbObjectType.TextSearchDictionary => $"DROP TEXT SEARCH DICTIONARY {name};",
            DbObjectType.TextSearchConfiguration => $"DROP TEXT SEARCH CONFIGURATION {name};",
            DbObjectType.OperatorClass => $"DROP OPERATOR CLASS {name} USING {PgDdl.Q(row.Signature ?? "")};",
            DbObjectType.Cast => $"DROP CAST ({row.Name});",
            _ => $"DROP SCHEMA {name};",
        };
        return $"-- {DropWarning}\n-- {row.TypeLabel} {row.FullName.ReplaceLineEndings(" ")} exists only in {targetLabel.ReplaceLineEndings(" ")}.\n{statement}\n";
    }

    /// <summary>Runs the PostgreSQL generator for the row, the chosen dependencies and their schemas, and assembles the parts in run order.</summary>
    public static async Task<ObjectScriptOutcome> RunAsync(
        CompareSession session, CompareRow row, int index, List<CompareRow> included, ScriptEndpoint source, ScriptEndpoint target,
        IScriptGenerator generator, string root, IProgress<ScriptProgress>? progress, CancellationToken ct)
    {
        var scope = new List<CompareRow> { row };
        scope.AddRange(included);
        foreach (var r in scope.ToList())
            if (session.SourceIndex.TryGetValue(r.Key, out var obj))
                foreach (var (dep, required, _) in Needs(session, obj, index))
                    if (required && dep.Type == DbObjectType.Schema && scope.All(x => x.Key != dep.Key)) scope.Add(dep);

        var statuses = scope.Select(r => (r.Type, Status: StatusOf(r, index))).ToList();
        var sections = new List<ScriptSection>
        {
            ScriptSection.NewColumns, ScriptSection.MissingObjects, ScriptSection.ColumnTypes, ScriptSection.Constraints, ScriptSection.Indexes,
        };
        // 07 also holds a table's rules (CREATE RULE), for a table that differs or that 03 creates
        if (statuses.Any(s => (s.Type == DbObjectType.Trigger && s.Status == ObjectStatus.MissingInTarget) ||
                              (s.Type == DbObjectType.Table && s.Status is ObjectStatus.Different or ObjectStatus.MissingInTarget)))
            sections.Add(ScriptSection.Triggers);
        if (statuses.Any(s => s.Type.IsModule() && s.Status == ObjectStatus.Different)) sections.Add(ScriptSection.DifferentModules);
        if (statuses.Any(s => s.Type.IsModule() && s.Status == ObjectStatus.FormattingOnly)) sections.Add(ScriptSection.Formatting);

        static string Short(string s) => s.Length > 40 ? s[..40] : s;
        var options = new ScriptOptions
        {
            Sections = sections,
            OutputFolder = root,
            RunFolderName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Short(target.Label)}_{Short(row.FullName)}",
            OnlyKeys = [.. scope.Select(r => r.Key)],
        };
        var result = session.Result!;
        var outcome = await generator.GenerateAsync(new ScriptRequestContext(source, target, session.Source,
            index < session.Targets.Count ? session.Targets[index] : null, result, index, options,
            new HashSet<string>(), new Dictionary<string, LineChange>(), session.Options), ct, progress);
        return Assemble(outcome, row, target.Label, [.. scope.Skip(1).Select(r => r.Key)]);
    }

    /// <summary>The API answer from the generator's outcome: the objects\ files in run order and their text, and the holds.</summary>
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
            Generated = outcome,
        };
        var v = outcome.Validation;
        result.Validation = new ObjectScriptValidation
        {
            Passed = v.Passed,
            Summary = v.Level is { Parsed: false } level
                ? $"{level.NotValidatedReason} {v.Batches} statements, {v.SyntaxErrors} lexical errors."
                : $"{(v.Passed ? "Passed" : "Problems found")}: {v.Batches} statements parsed with {v.Level?.Parser ?? "the PostgreSQL parser"}, " +
                  $"{v.SyntaxErrors} syntax errors; {v.ModulesExact} of {v.ModulesChecked} code bodies are the source's text exactly " +
                  "(not compile-checked)." + (v.Problems.Count > 0 ? " " + string.Join(" ", v.Problems.Take(3)) : ""),
        };
        if (!outcome.Success) return result;

        int Order(WrittenFile f) => Array.FindIndex(RunOrder, r => string.Equals(r, f.Folder, StringComparison.OrdinalIgnoreCase));
        static string Name(WrittenFile f) => f.Object?.ToString() ?? Path.GetFileNameWithoutExtension(f.RelativePath);
        var script = new StringBuilder();
        foreach (var f in outcome.Files.Where(f => f.IsObjectFile && f.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && Order(f) >= 0)
                     .OrderBy(Order).ThenBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            result.Parts.Add(new ObjectScriptPart
            {
                Folder = f.Folder,
                File = f.FullPath,
                ObjectName = Name(f),
                Kind = JsonNamingPolicy.CamelCase.ConvertName(f.Kind.ToString()),
            });
            var text = ScriptWriter.ReadBack(f.FullPath);
            script.Append($"-- {f.Folder}: {Name(f).ReplaceLineEndings(" ")}\n").Append(text).Append(text.EndsWith('\n') ? "\n" : "\n\n");
        }
        result.Script = script.ToString();

        var held = result.HeldBack.Count;
        result.Message = result.Parts.Count > 0
            ? $"Scripted {row.FullName} for {targetLabel}: {result.Parts.Count} file(s)" + (held > 0 ? $"; {held} item(s) held back - see why below." : ".")
            : held > 0
                ? $"Nothing was scripted for {row.FullName}: {held} item(s) held back - see why below."
                : $"Nothing was scripted for {row.FullName}: the generator found nothing it can change for it in {targetLabel}.";
        return result;
    }
}
