using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SchemaScope.Scripting;

/// <summary>
/// The parameter list of a procedure or function, read from its CREATE text with ScriptDom (offline).
/// sys.parameters cannot be used for this: it does not record whether a T-SQL parameter has a default.
/// 10_different_modules uses it to say how replacing a module changes what its callers must send.
/// </summary>
internal static class ModuleSignature
{
    public sealed record Param(string Name, string Type, bool HasDefault, bool IsOutput);

    /// <summary>
    /// The parameters in declaration order; empty for a view or trigger; null when the text does not parse
    /// or is not a single CREATE / ALTER of a module.
    /// </summary>
    public static IReadOnlyList<Param>? Of(ModuleRow module)
    {
        if (module.Definition is null) return null;
        var parser = new TSql160Parser(initialQuotedIdentifiers: module.QuotedId);
        using var reader = new StringReader(module.Definition);
        if (parser.Parse(reader, out var errors) is not TSqlScript script || errors.Count > 0) return null;
        var statement = script.Batches.SelectMany(b => b.Statements).FirstOrDefault();
        IList<ProcedureParameter>? parameters = statement switch
        {
            ProcedureStatementBody p => p.Parameters,
            FunctionStatementBody f => f.Parameters,
            ViewStatementBody or TriggerStatementBody => [],
            _ => null,
        };
        return parameters?.Select(p => new Param(
            p.VariableName.Value,
            TypeText(p.DataType),
            p.Value is not null,
            p.Modifier == ParameterModifier.Output)).ToList();
    }

    /// <summary>
    /// What a caller of the target's current module would have to change once the source's text replaces it,
    /// as plain sentences; empty when the parameter list is the same for callers.
    /// </summary>
    public static List<string> CallerChanges(ModuleRow source, ModuleRow target)
    {
        var otype = source.OType.Trim().ToUpperInvariant();
        if (otype is "V" or "TR") return [];
        var src = Of(source);
        var tgt = Of(target);
        if (src is null || tgt is null) return ["could not read the parameter list - compare the two texts"];

        static string List(IReadOnlyList<Param> ps) => ps.Count == 0 ? "none" : string.Join(", ", ps.Select(p => p.Name));
        static bool Same(Param a, Param b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        var changes = new List<string>();
        if (otype is "FN" or "IF" or "TF")
        {
            // a function call must pass every argument (DEFAULT included), in order
            var sameShape = src.Count == tgt.Count && src.Zip(tgt).All(z => Same(z.First, z.Second) &&
                string.Equals(z.First.Type, z.Second.Type, StringComparison.OrdinalIgnoreCase));
            if (!sameShape)
                changes.Add($"parameters change from ({List(tgt)}) to ({List(src)}) - every call must pass all of a function's arguments, so its callers must change");
            return changes;
        }

        foreach (var t in tgt)
            if (!src.Any(s => Same(s, t)))
                changes.Add($"{t.Name} is removed - a caller that still sends it fails (Msg 8145)");
        foreach (var s in src)
        {
            var t = tgt.FirstOrDefault(x => Same(s, x));
            if (t is null)
            {
                if (!s.HasDefault) changes.Add($"new {s.Name} has no default - a caller that does not send it fails (Msg 201)");
                continue;
            }
            if (!string.Equals(s.Type, t.Type, StringComparison.OrdinalIgnoreCase))
                changes.Add($"{s.Name} changes type from {t.Type} to {s.Type}");
            if (s.IsOutput != t.IsOutput)
                changes.Add(s.IsOutput ? $"{s.Name} becomes OUTPUT" : $"{s.Name} is no longer OUTPUT - a caller that reads it back gets nothing");
            if (t.HasDefault && !s.HasDefault)
                changes.Add($"{s.Name} loses its default - a caller that does not send it fails (Msg 201)");
        }
        var keptInTarget = tgt.Where(t => src.Any(s => Same(s, t))).Select(t => t.Name.ToLowerInvariant());
        var keptInSource = src.Where(s => tgt.Any(t => Same(s, t))).Select(s => s.Name.ToLowerInvariant());
        if (!keptInTarget.SequenceEqual(keptInSource))
            changes.Add("the parameter order changes - a caller that passes values by position sends them to the wrong parameters");
        return changes;
    }

    /// <summary>The data type as written, lower-cased, without spaces, e.g. <c>nvarchar(50)</c> or <c>dbo.mytype</c>.</summary>
    private static string TypeText(TSqlFragment? type)
    {
        if (type is null || type.FirstTokenIndex < 0) return "";
        var tokens = type.ScriptTokenStream;
        var text = string.Concat(Enumerable.Range(type.FirstTokenIndex, type.LastTokenIndex - type.FirstTokenIndex + 1)
            .Select(i => tokens[i])
            .Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            .Select(t => t.Text));
        return text.Replace("[", "").Replace("]", "").ToLowerInvariant();
    }
}
