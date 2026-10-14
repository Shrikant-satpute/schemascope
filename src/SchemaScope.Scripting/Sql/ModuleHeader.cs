using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SchemaScope.Scripting;

/// <summary>
/// The name in a module's <c>CREATE</c> header. sp_rename changes sys.objects but not the stored text, so a
/// renamed module's definition still says <c>CREATE PROCEDURE old_name</c>; replaying that text creates the
/// old name. <see cref="WithCatalogName"/> puts the catalog name back into the header, and nothing else.
/// </summary>
internal static class ModuleHeader
{
    /// <summary>
    /// The definition with its header name replaced by <c>[schema].[name]</c> of <paramref name="module"/>, and
    /// the name the header had (as written); null when the header already names it, no header is found, or it is
    /// not a one- or two-part name. The name is compared exactly (a case-only rename counts), the schema ignoring case; a header without a
    /// schema is left alone when its name matches.
    /// </summary>
    public static (string Definition, string Was)? WithCatalogName(ModuleRow module)
    {
        if (module.Definition is not { } text) return null;
        var parser = new TSql160Parser(initialQuotedIdentifiers: module.QuotedId);
        using var reader = new StringReader(text);
        var tokens = parser.GetTokenStream(reader, out _).Where(t => !IsTrivia(t.TokenType)).ToList();

        // CREATE [OR ALTER] | ALTER, then PROC / PROCEDURE / FUNCTION / VIEW / TRIGGER: the first statement only
        var i = 0;
        if (At(tokens, i, TSqlTokenType.Create))
        {
            i++;
            if (At(tokens, i, TSqlTokenType.Or) && At(tokens, i + 1, TSqlTokenType.Alter)) i += 2;
        }
        else if (At(tokens, i, TSqlTokenType.Alter)) i++;
        else return null;
        if (!(At(tokens, i, TSqlTokenType.Proc) || At(tokens, i, TSqlTokenType.Procedure) || At(tokens, i, TSqlTokenType.Function) ||
              At(tokens, i, TSqlTokenType.View) || At(tokens, i, TSqlTokenType.Trigger))) return null;
        i++;

        // name [. name]
        var parts = new List<TSqlParserToken>();
        while (i < tokens.Count && IsName(tokens[i].TokenType))
        {
            parts.Add(tokens[i++]);
            if (!At(tokens, i, TSqlTokenType.Dot)) break;
            i++;
        }
        if (parts.Count is 0 or > 2) return null;

        var name = Unquote(parts[^1].Text);
        var schema = parts.Count == 2 ? Unquote(parts[0].Text) : null;
        if (string.Equals(name, module.Obj, StringComparison.Ordinal) &&
            (schema is null || string.Equals(schema, module.Sch, StringComparison.OrdinalIgnoreCase))) return null;

        var start = parts[0].Offset;
        var end = parts[^1].Offset + parts[^1].Text.Length;
        return (text[..start] + Ddl.Bracket(module.Sch, module.Obj) + text[end..], string.Join(".", parts.Select(p => p.Text)));
    }

    private static bool At(List<TSqlParserToken> t, int i, TSqlTokenType type) => i < t.Count && t[i].TokenType == type;

    private static bool IsTrivia(TSqlTokenType t) =>
        t is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    private static bool IsName(TSqlTokenType t) =>
        t is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier or TSqlTokenType.AsciiStringOrQuotedIdentifier;

    /// <summary><c>[a]]b]</c> → <c>a]b</c>, <c>"a""b"</c> → <c>a"b</c>, a plain word unchanged.</summary>
    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '[' && s[^1] == ']' ? s[1..^1].Replace("]]", "]")
        : s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\"\"", "\"")
        : s;
}
