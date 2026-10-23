using System.Text;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// Where a PostgreSQL statement ends, by psql's own rule (psqlscan.l), so a file splits here exactly as
/// <c>psql -f</c> sends it: at a <c>;</c> outside quotes, comments, parentheses and a SQL-standard routine body.
///
///   'it''s'  E'a\'b'     strings (an E string also takes backslash escapes)
///   "a;b"               a quoted name
///   $tag$ ... $tag$     a dollar-quoted body, whatever is inside
///   -- ...   /* /* */ */ comments; block comments nest
///   ( ... )             a ; inside parentheses does not end the statement
///   CREATE [OR REPLACE] FUNCTION|PROCEDURE ... BEGIN ATOMIC ... END
///                       psql counts BEGIN, CASE (inside a BEGIN) and END outside parentheses in such a statement
///
/// Each statement keeps the line it starts on (its first token, after any comments) and runs through its <c>;</c>.
/// The validator and (from R3) the apply use this one instance, so what was checked is what runs.
/// </summary>
public sealed class PgStatementSplitter : IStatementSplitter
{
    public static PgStatementSplitter Instance { get; } = new();

    public IReadOnlyList<SqlBatch> Split(string text)
    {
        var output = new List<SqlBatch>();
        text ??= "";
        var line = 1;
        int? start = null;
        var startLine = 0;
        int paren = 0, begin = 0, identifiers = 0;
        var firsts = new char[4];

        void Reset()
        {
            start = null;
            paren = begin = identifiers = 0;
            Array.Clear(firsts);
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && At(text, i + 1) == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && At(text, i + 1) == '*')
            {
                var end = Or(BlockCommentEnd(text, i), text);
                line += Lines(text, i, end);
                i = end;
                continue;
            }

            if (start is null)
            {
                start = i;
                startLine = line;
            }

            if (c == ';')
            {
                i++;
                if (paren == 0 && begin == 0)
                {
                    output.Add(new SqlBatch(text[start.Value..i], startLine));
                    Reset();
                }
                continue;
            }
            if (c == '(') { paren++; i++; continue; }
            if (c == ')') { if (paren > 0) paren--; i++; continue; }

            int stop;
            if ((c is 'E' or 'e') && At(text, i + 1) == '\'' && !IsIdentCont(At(text, i - 1)))
                stop = Or(QuotedEnd(text, i + 1, '\'', backslash: true), text);
            else if (c == '\'')
                stop = Or(QuotedEnd(text, i, '\'', backslash: false), text);
            else if (c == '"')
                stop = Or(QuotedEnd(text, i, '"', backslash: false), text);
            else if (c == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                stop = close < 0 ? text.Length : close + tag.Length;
            }
            else if (IsIdentStart(c))
            {
                stop = i + 1;
                while (stop < text.Length && IsIdentCont(text[stop])) stop++;
                Identifier(text.AsSpan(i, stop - i));
                i = stop;
                continue;
            }
            else
            {
                i++;
                continue;
            }
            line += Lines(text, i, stop);
            i = stop;
        }

        if (start is { } tail && !string.IsNullOrWhiteSpace(text[tail..]))
            output.Add(new SqlBatch(text[tail..], startLine));
        return output;

        // psql: track CREATE [OR REPLACE] FUNCTION|PROCEDURE by the first letters of its first words, and inside such a
        // statement count BEGIN / CASE / END outside parentheses
        void Identifier(ReadOnlySpan<char> word)
        {
            if (identifiers < firsts.Length &&
                (Is(word, "create") || Is(word, "function") || Is(word, "procedure") || Is(word, "or") || Is(word, "replace")))
                firsts[identifiers] = char.ToLowerInvariant(word[0]);
            identifiers++;

            var routine = firsts[0] == 'c' &&
                          (firsts[1] is 'f' or 'p' || (firsts[1] == 'o' && firsts[2] == 'r' && firsts[3] is 'f' or 'p'));
            if (!routine || paren != 0) return;
            if (Is(word, "begin")) begin++;
            else if (Is(word, "case")) { if (begin >= 1) begin++; }
            else if (Is(word, "end")) { if (begin > 0) begin--; }
        }
    }

    /// <summary>
    /// The text with every comment and literal (string, quoted name, dollar body) replaced by spaces, newlines kept, so
    /// words found in it are code and line numbers still match.
    /// </summary>
    public static string CodeOnly(string text)
    {
        text ??= "";
        var sb = new StringBuilder(text.Length);
        var i = 0;
        void Blank(int from, int to)
        {
            for (var k = from; k < to; k++) sb.Append(text[k] == '\n' ? '\n' : ' ');
        }
        while (i < text.Length)
        {
            var c = text[i];
            int stop;
            if (c == '-' && At(text, i + 1) == '-')
            {
                stop = text.IndexOf('\n', i);
                if (stop < 0) stop = text.Length;
            }
            else if (c == '/' && At(text, i + 1) == '*') stop = Or(BlockCommentEnd(text, i), text);
            else if ((c is 'E' or 'e') && At(text, i + 1) == '\'' && !IsIdentCont(At(text, i - 1)))
                stop = Or(QuotedEnd(text, i + 1, '\'', backslash: true), text);
            else if (c == '\'') stop = Or(QuotedEnd(text, i, '\'', backslash: false), text);
            else if (c == '"') stop = Or(QuotedEnd(text, i, '"', backslash: false), text);
            else if (c == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                stop = close < 0 ? text.Length : close + tag.Length;
            }
            else if (IsIdentStart(c))
            {
                stop = i + 1;
                while (stop < text.Length && IsIdentCont(text[stop])) stop++;
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }
            else
            {
                sb.Append(c);
                i++;
                continue;
            }
            Blank(i, stop);
            i = stop;
        }
        return sb.ToString();
    }

    /// <summary>Null, or what is left open at the end of the text (a string, quoted name, dollar body or block comment).</summary>
    public static string? Unterminated(string text)
    {
        text ??= "";
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '-' && At(text, i + 1) == '-')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl;
                continue;
            }
            if (c == '/' && At(text, i + 1) == '*')
            {
                var end = BlockCommentEnd(text, i);
                if (end < 0) return "a /* comment is not closed";
                i = end;
                continue;
            }
            var e = (c is 'E' or 'e') && At(text, i + 1) == '\'' && !IsIdentCont(At(text, i - 1));
            if (e || c is '\'' or '"')
            {
                var open = e ? i + 1 : i;
                var end = QuotedEnd(text, open, text[open], backslash: e);
                if (end < 0) return text[open] == '"' ? "a quoted name is not closed" : "a string is not closed";
                i = end;
                continue;
            }
            if (c == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (close < 0) return $"the {tag} body is not closed";
                i = close + tag.Length;
                continue;
            }
            if (IsIdentStart(c))
            {
                i++;
                while (i < text.Length && IsIdentCont(text[i])) i++;
                continue;
            }
            i++;
        }
        return null;
    }

    private static bool Is(ReadOnlySpan<char> word, string what) => word.Equals(what, StringComparison.OrdinalIgnoreCase);

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    private static int Lines(string s, int from, int to)
    {
        var n = 0;
        for (var k = from; k < to && k < s.Length; k++) if (s[k] == '\n') n++;
        return n;
    }

    /// <summary>psql's ident_start: a letter, _ or any non-ASCII character.</summary>
    internal static bool IsIdentStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= '\u0080';

    /// <summary>psql's ident_cont: ident_start, a digit or $.</summary>
    internal static bool IsIdentCont(char c) => IsIdentStart(c) || c is >= '0' and <= '9' or '$';

    /// <summary>The index just past a nested block comment starting at <paramref name="i"/>; -1 when it is not closed.</summary>
    private static int BlockCommentEnd(string s, int i)
    {
        var level = 0;
        var j = i;
        while (j < s.Length)
        {
            if (j + 1 < s.Length && s[j] == '/' && s[j + 1] == '*') { level++; j += 2; continue; }
            if (j + 1 < s.Length && s[j] == '*' && s[j + 1] == '/') { level--; j += 2; if (level == 0) return j; continue; }
            j++;
        }
        return -1;
    }

    /// <summary>The index just past the closing quote (doubled quotes, and backslashes for E'', stay inside); -1 when unclosed.</summary>
    private static int QuotedEnd(string s, int open, char quote, bool backslash)
    {
        var j = open + 1;
        while (j < s.Length)
        {
            if (backslash && s[j] == '\\') { j += 2; continue; }
            if (s[j] == quote)
            {
                if (j + 1 < s.Length && s[j + 1] == quote) { j += 2; continue; }
                return j + 1;
            }
            j++;
        }
        return -1;
    }

    /// <summary>Where a token that may be left open ends: <paramref name="end"/>, or the end of the text.</summary>
    private static int Or(int end, string s) => end < 0 ? s.Length : end;

    /// <summary>"$tag$" or "$$" at <paramref name="i"/> (psql's dolqdelim); null for $1 and for a $ inside a name.</summary>
    private static string? DollarTag(string s, int i)
    {
        if (IsIdentCont(At(s, i - 1))) return null;
        var j = i + 1;
        if (j < s.Length && IsIdentStart(s[j]))
            while (j < s.Length && (IsIdentStart(s[j]) || s[j] is >= '0' and <= '9')) j++;
        return j < s.Length && s[j] == '$' ? s[i..(j + 1)] : null;
    }
}
