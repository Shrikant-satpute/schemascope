using System.Text;

namespace SchemaScope.PostgreSql;

/// <summary>
/// PostgreSQL-aware text helpers for the compare: comments, whitespace and case are found by a small lexer that
/// knows the dialect's quoting, so nothing inside a literal is ever taken for formatting.
///
///   'it''s'         a string; doubled quotes inside
///   E'a\'b'         an escape string; backslash escapes too
///   "Users"         a quoted identifier: case kept, it is not users
///   $tag$ ... $tag$ a dollar-quoted string. At the top level that is a routine body, so its inside is read as code
///                   (comments in it are comments); one nested inside a body is a literal and kept as it is.
///   /* /* */ */     block comments nest
///   -- ...          a line comment
/// </summary>
public static class PgText
{
    /// <summary>
    /// BOM, line endings, trailing spaces on each line and trailing blank lines - nothing that changes meaning.
    /// Unlike T-SQL there is no header verb to fold: the catalog's own deparse always says CREATE (OR REPLACE).
    /// </summary>
    public static string Light(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return "";
        var s = sql[0] == '﻿' ? sql[1..] : sql;
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(s.Length);
        foreach (var line in lines) sb.Append(line.TrimEnd(' ', '\t')).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Every comment removed (a line comment's newline is kept), literals and quoted names untouched.</summary>
    public static string StripComments(string? sql) => string.IsNullOrEmpty(sql) ? "" : Walk(sql, loosen: false, depth: 0);

    /// <summary>
    /// The aggressive form behind "only formatting differs": no comments, whitespace collapsed (and dropped beside
    /// brackets, commas and semicolons), unquoted words lower-cased - PostgreSQL folds them so, which makes this a
    /// fact rather than a guess. String literals and "quoted" names are kept exactly, so 'A' never equals 'a' and
    /// "Users" never equals users.
    /// </summary>
    public static string Loosen(string? sql) => string.IsNullOrEmpty(sql) ? "" : Walk(sql, loosen: true, depth: 0).Trim();

    private static string Walk(string s, bool loosen, int depth)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        var i = 0;

        void Emit(string text, bool isWord)
        {
            if (loosen && pendingSpace && sb.Length > 0 && text.Length > 0 && KeepSpace(sb[^1], text[0]))
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(loosen && isWord ? text.ToLowerInvariant() : text);
        }

        while (i < s.Length)
        {
            var c = s[i];

            // -- line comment
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var end = s.IndexOf('\n', i);
                if (end < 0) end = s.Length;
                if (loosen) pendingSpace = true;
                else if (end < s.Length) sb.Append('\n');
                i = end < s.Length ? end + 1 : end;
                continue;
            }

            // /* block comment */, nesting
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var level = 0;
                var j = i;
                while (j < s.Length)
                {
                    if (j + 1 < s.Length && s[j] == '/' && s[j + 1] == '*') { level++; j += 2; continue; }
                    if (j + 1 < s.Length && s[j] == '*' && s[j + 1] == '/') { level--; j += 2; if (level == 0) break; continue; }
                    j++;
                }
                if (loosen) pendingSpace = true;
                else sb.Append(' ');
                i = j;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (loosen) pendingSpace = true;
                else sb.Append(c);
                i++;
                continue;
            }

            // E'...' / e'...': backslash escapes
            if ((c is 'E' or 'e') && i + 1 < s.Length && s[i + 1] == '\'' && !IsWordChar(Prev(s, i)))
            {
                var end = QuotedEnd(s, i + 1, '\'', backslash: true);
                Emit(s[i..end], isWord: false);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                var end = QuotedEnd(s, i, '\'', backslash: false);
                Emit(s[i..end], isWord: false);
                i = end;
                continue;
            }

            if (c == '"')
            {
                var end = QuotedEnd(s, i, '"', backslash: false);
                Emit(s[i..end], isWord: false);
                i = end;
                continue;
            }

            if (c == '$' && DollarTag(s, i) is { } tag)
            {
                var bodyStart = i + tag.Length;
                var close = s.IndexOf(tag, bodyStart, StringComparison.Ordinal);
                var bodyEnd = close < 0 ? s.Length : close;
                var end = close < 0 ? s.Length : close + tag.Length;
                if (depth == 0)
                {
                    // a routine body: code, read the same way; the tag itself is spelling, so the loose form says $$
                    var body = Walk(s[bodyStart..bodyEnd], loosen, depth + 1);
                    Emit(loosen ? "$$" + body.Trim() + "$$" : tag + body + (close < 0 ? "" : tag), isWord: false);
                }
                else
                {
                    Emit(s[i..end], isWord: false);
                }
                i = end;
                continue;
            }

            if (IsWordChar(c))
            {
                var j = i;
                while (j < s.Length && (IsWordChar(s[j]) || s[j] == '$')) j++;
                Emit(s[i..j], isWord: true);
                i = j;
                continue;
            }

            Emit(c.ToString(), isWord: false);
            i++;
        }

        return sb.ToString();
    }

    private static char Prev(string s, int i) => i > 0 ? s[i - 1] : ' ';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Whitespace matters only between two words (or literals), and between two operator characters, which would
    /// otherwise read as one longer operator ("&lt; -1" is not "&lt;-1"). Anywhere else it is layout.
    /// </summary>
    private static bool KeepSpace(char before, char after) =>
        (WordLike(before) && WordLike(after)) || (IsOperatorChar(before) && IsOperatorChar(after));

    private static bool WordLike(char c) => IsWordChar(c) || c is '$' or '\'' or '"';

    private static bool IsOperatorChar(char c) => "+-*/<>=~!@#%^&|`?".Contains(c);

    /// <summary>The index just past the closing quote (doubled quotes, and backslashes for E'', stay inside).</summary>
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
        return s.Length;
    }

    /// <summary>"$tag$" or "$$" starting at <paramref name="i"/>; null for $1 and for a $ inside a name.</summary>
    private static string? DollarTag(string s, int i)
    {
        if (IsWordChar(Prev(s, i)) || Prev(s, i) == '$') return null;
        var j = i + 1;
        if (j < s.Length && char.IsDigit(s[j])) return null;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        return j < s.Length && s[j] == '$' ? s[i..(j + 1)] : null;
    }
}
