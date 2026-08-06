using System.Text;

namespace SchemaScope.Core.Normalization;

/// <summary>
/// Small, fast, T-SQL aware text helpers. These run over every object in the
/// database, so they are hand written scanners rather than regexes.
/// </summary>
public static class TSqlText
{
    /// <summary>
    /// Light clean up that never changes meaning: BOM, line endings, trailing
    /// spaces on each line, trailing blank lines, and ALTER/CREATE OR ALTER
    /// headers folded to plain CREATE (sys.sql_modules keeps whichever verb was
    /// last executed, which differs harmlessly between servers).
    /// </summary>
    public static string Light(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return "";

        var s = sql;
        if (s[0] == '﻿') s = s[1..];

        var sb = new StringBuilder(s.Length);
        var lineStart = 0;
        var lastNonSpace = -1;

        for (var i = 0; i <= s.Length; i++)
        {
            var atEnd = i == s.Length;
            var c = atEnd ? '\n' : s[i];

            if (c is '\r' or '\n')
            {
                // append the line without its trailing whitespace
                var end = lastNonSpace >= lineStart ? lastNonSpace + 1 : lineStart;
                sb.Append(s, lineStart, end - lineStart);
                sb.Append('\n');

                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                lineStart = i + 1;
                lastNonSpace = -1;
                continue;
            }

            if (c is not (' ' or '\t')) lastNonSpace = i;
        }

        // drop trailing blank lines
        var text = sb.ToString();
        var cut = text.Length;
        while (cut > 0 && text[cut - 1] == '\n') cut--;
        text = text[..cut];

        return FoldCreateOrAlter(text);
    }

    /// <summary>
    /// Rewrites the object header verb to CREATE. Only the first
    /// CREATE/ALTER/CREATE OR ALTER followed by a module keyword is touched, so
    /// an ALTER TABLE inside the body is left alone.
    /// </summary>
    public static string FoldCreateOrAlter(string sql)
    {
        var i = SkipLeadingTrivia(sql, 0);
        if (i >= sql.Length) return sql;

        var (verb, afterVerb) = ReadWord(sql, i);
        var isCreate = verb.Equals("CREATE", StringComparison.OrdinalIgnoreCase);
        var isAlter = verb.Equals("ALTER", StringComparison.OrdinalIgnoreCase);
        if (!isCreate && !isAlter) return sql;

        var j = SkipLeadingTrivia(sql, afterVerb);
        var (next, afterNext) = ReadWord(sql, j);

        // CREATE OR ALTER <module>
        if (isCreate && next.Equals("OR", StringComparison.OrdinalIgnoreCase))
        {
            var k = SkipLeadingTrivia(sql, afterNext);
            var (alter, afterAlter) = ReadWord(sql, k);
            if (!alter.Equals("ALTER", StringComparison.OrdinalIgnoreCase)) return sql;
            var m = SkipLeadingTrivia(sql, afterAlter);
            var (kw, _) = ReadWord(sql, m);
            return IsModuleKeyword(kw) ? string.Concat(sql[..i], "CREATE ", sql[m..]) : sql;
        }

        if (!IsModuleKeyword(next)) return sql;
        return isAlter ? string.Concat(sql[..i], "CREATE ", sql[j..]) : sql;
    }

    private static bool IsModuleKeyword(string w) =>
        w.Equals("PROC", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("VIEW", StringComparison.OrdinalIgnoreCase) ||
        w.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase);

    private static (string Word, int Next) ReadWord(string s, int i)
    {
        var start = i;
        while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_')) i++;
        return (s[start..i], i);
    }

    /// <summary>Skips whitespace and comments starting at <paramref name="i"/>.</summary>
    private static int SkipLeadingTrivia(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }

            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }

            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }

            break;
        }
        return i;
    }

    /// <summary>
    /// Removes comments while respecting string literals, [bracket] identifiers
    /// and "quoted" identifiers. T-SQL block comments nest, so we track depth.
    /// </summary>
    public static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append(sql[++i]); }
                        else break;
                    }
                    i++;
                }
                continue;
            }

            if (c is '[' or '"')
            {
                var close = c == '[' ? ']' : '"';
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == close)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == close) { sb.Append(sql[++i]); }
                        else break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                if (i < sql.Length) sb.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                i--;
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collapses every run of whitespace to a single space and trims. Token
    /// boundaries are preserved, so this cannot merge two words into one.
    /// </summary>
    public static string CollapseWhitespace(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var pendingSpace = false;

        foreach (var c in sql)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var n = 1;
        foreach (var c in text) if (c == '\n') n++;
        return n;
    }
}
