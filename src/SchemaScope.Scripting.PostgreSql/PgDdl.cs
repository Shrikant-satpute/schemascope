using System.Text.RegularExpressions;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>Quoting and small text helpers for the PostgreSQL scripts. Every generated name is double-quoted.</summary>
public static partial class PgDdl
{
    /// <summary>"name" with " doubled.</summary>
    public static string Q(string name) => PgCanonicalWriter.Q(name);

    /// <summary>"schema"."name".</summary>
    public static string Q(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    /// <summary>The object's quoted name (a schema's own name for a schema).</summary>
    public static string Q(DbObject o) => o.Type == DbObjectType.Schema ? Q(o.Name) : Q(o.Schema, o.Name);

    /// <summary>A string literal: 'text' with ' doubled (standard_conforming_strings, on by default since 9.1).</summary>
    public static string Lit(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    /// <summary>
    /// A name as quote_ident spells it in the server's own deparse (format_type, pg_get_expr): bare when it is lower-case
    /// letters, digits, _ and $ and does not start with a digit or $, quoted otherwise. (A reserved word is quoted by the
    /// server too; <see cref="Mentions"/> looks for both spellings.)
    /// </summary>
    public static string Ident(string name) => BareRx().IsMatch(name) ? name : Q(name);

    /// <summary>True when the deparsed <paramref name="text"/> names schema.name, bare or quoted, as a whole name.</summary>
    public static bool Mentions(string? text, string schema, string name)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var form in new[] { $"{Ident(schema)}.{Ident(name)}", Q(schema, name), $"{Ident(schema)}.{Q(name)}", $"{Q(schema)}.{Ident(name)}" }.Distinct())
        {
            for (var at = text.IndexOf(form, StringComparison.Ordinal); at >= 0; at = text.IndexOf(form, at + 1, StringComparison.Ordinal))
            {
                var before = at > 0 ? text[at - 1] : ' ';
                var after = at + form.Length < text.Length ? text[at + form.Length] : ' ';
                if (!PgStatementSplitter.IsIdentCont(before) && before != '.' && before != '"' &&
                    !PgStatementSplitter.IsIdentCont(after) && after != '"')
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="text"/> names the column <paramref name="column"/> as a word (bare or quoted), outside literals.</summary>
    public static bool MentionsColumn(string? text, string column)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var code = PgStatementSplitter.CodeOnly(text);
        if (text.Contains(Q(column), StringComparison.Ordinal)) return true;
        var bare = Ident(column);
        if (bare.StartsWith('"')) return false;
        for (var at = code.IndexOf(bare, StringComparison.Ordinal); at >= 0; at = code.IndexOf(bare, at + 1, StringComparison.Ordinal))
        {
            var before = at > 0 ? code[at - 1] : ' ';
            var after = at + bare.Length < code.Length ? code[at + bare.Length] : ' ';
            if (!PgStatementSplitter.IsIdentCont(before) && !PgStatementSplitter.IsIdentCont(after)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> qualifies a name with schema <paramref name="schema"/> (bare or quoted, then a
    /// dot) anywhere, a routine's body and literals included: a body names what it calls in its own text.
    /// </summary>
    public static bool MentionsSchema(string? text, string schema)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var code = text;
        foreach (var form in new[] { Ident(schema) + ".", Q(schema) + "." }.Distinct())
            for (var at = code.IndexOf(form, StringComparison.Ordinal); at >= 0; at = code.IndexOf(form, at + 1, StringComparison.Ordinal))
            {
                var before = at > 0 ? code[at - 1] : ' ';
                if (!PgStatementSplitter.IsIdentCont(before) && before != '.' && before != '"') return true;
            }
        return false;
    }

    /// <summary>The statement with exactly one terminating semicolon and a newline.</summary>
    public static string Statement(string sql)
    {
        var t = (sql ?? "").TrimEnd();
        return (t.EndsWith(';') ? t : t + ";") + "\n";
    }

    /// <summary>For a -- comment: one line.</summary>
    public static string OneLine(string s) => (s ?? "").ReplaceLineEndings(" ");

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareRx();
}
