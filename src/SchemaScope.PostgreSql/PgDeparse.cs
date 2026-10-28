using System.Text;

namespace SchemaScope.PostgreSql;

/// <summary>
/// The server's deparse (pg_get_expr, pg_get_viewdef, pg_get_constraintdef, pg_get_function_arguments) spelled the
/// same way whatever the server's major version, so one schema built on PostgreSQL 13 and on 16 reads as the same
/// text. Only what a newer server merely prints differently is rewritten, never what it means differently:
///
///   SQL-standard syntax   14 and later print (now() AT TIME ZONE 'utc'::text), SUBSTRING(a FROM 1 FOR 2),
///                         POSITION((b) IN (a)), TRIM(BOTH b FROM a) and OVERLAY(a PLACING b FROM c) for what 13
///                         prints as timezone(...), "substring"(...), "position"(...), btrim(...) and "overlay"(...).
///                         Both mean one function call, so both are read as the function call (the spelling every
///                         version understands, and the one an explicit call has on any version).
///   IN parameters         14 and later write a procedure's IN mode out ("IN p integer"); 13 leaves it implied.
///   view column prefixes  before 16 a view that reads one relation still qualifies every column with it
///                         ("SELECT t.id FROM s.t t"); 16 and later leave the prefix out there ("SELECT id ...").
///
/// EXTRACT is not touched: 13's date_part returns double precision and 14's extract numeric, which is a real change.
/// Anything this cannot read for sure is left exactly as it is.
/// </summary>
public static class PgDeparse
{
    private enum K { Space, Word, Quoted, Str, Punct }

    private readonly record struct Tok(K Kind, int Start, int Length)
    {
        public int End => Start + Length;
    }

    private static List<Tok> Tokens(string s)
    {
        var list = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                list.Add(new Tok(K.Space, start, i - start));
                continue;
            }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                while (i < s.Length && s[i] != '\n') i++;
                list.Add(new Tok(K.Space, start, i - start));
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var level = 0;
                while (i < s.Length)
                {
                    if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') { level++; i += 2; continue; }
                    if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '/') { level--; i += 2; if (level == 0) break; continue; }
                    i++;
                }
                list.Add(new Tok(K.Space, start, i - start));
                continue;
            }
            if ((c is 'E' or 'e') && i + 1 < s.Length && s[i + 1] == '\'' && (i == 0 || !IsWordChar(s[i - 1])))
            {
                i = Quoted(s, i + 1, '\'', backslash: true);
                list.Add(new Tok(K.Str, start, i - start));
                continue;
            }
            if (c == '\'')
            {
                i = Quoted(s, i, '\'', backslash: false);
                list.Add(new Tok(K.Str, start, i - start));
                continue;
            }
            if (c == '"')
            {
                i = Quoted(s, i, '"', backslash: false);
                list.Add(new Tok(K.Quoted, start, i - start));
                continue;
            }
            if (c == '$' && DollarTag(s, i) is { } tag)
            {
                var close = s.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? s.Length : close + tag.Length;
                list.Add(new Tok(K.Str, start, i - start));
                continue;
            }
            if (IsWordChar(c))
            {
                while (i < s.Length && (IsWordChar(s[i]) || s[i] == '$')) i++;
                list.Add(new Tok(K.Word, start, i - start));
                continue;
            }
            list.Add(new Tok(K.Punct, start, 1));
            i++;
        }
        return list;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 127;

    private static int Quoted(string s, int open, char quote, bool backslash)
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

    private static string? DollarTag(string s, int i)
    {
        if (i > 0 && (IsWordChar(s[i - 1]) || s[i - 1] == '$')) return null;
        var j = i + 1;
        if (j < s.Length && char.IsDigit(s[j])) return null;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        return j < s.Length && s[j] == '$' ? s[i..(j + 1)] : null;
    }

    private static bool Is(string s, Tok t, char c) => t.Kind == K.Punct && s[t.Start] == c;
    private static bool IsWord(string s, Tok t, string word) => t.Kind == K.Word && s.AsSpan(t.Start, t.Length).SequenceEqual(word);
    private static string Text(string s, Tok t) => s.Substring(t.Start, t.Length);

    /// <summary>The token index of the bracket that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int Closing(string s, List<Tok> toks, int open)
    {
        var depth = 0;
        for (var k = open; k < toks.Count; k++)
        {
            if (Is(s, toks[k], '(')) depth++;
            else if (Is(s, toks[k], ')') && --depth == 0) return k;
        }
        return -1;
    }

    /// <summary>The next token at or after <paramref name="k"/> that is not space, or -1.</summary>
    private static int NextSolid(List<Tok> toks, int k)
    {
        while (k < toks.Count && toks[k].Kind == K.Space) k++;
        return k < toks.Count ? k : -1;
    }

    private static int PrevSolid(List<Tok> toks, int k)
    {
        while (k >= 0 && toks[k].Kind == K.Space) k--;
        return k;
    }

    // ------------------------------------------------------------------ SQL-standard syntax

    private static readonly string[] Hints = [" AT TIME ZONE ", "SUBSTRING(", "POSITION(", "TRIM(", "OVERLAY("];

    /// <summary>
    /// <paramref name="text"/> with every SQL-standard function form a newer server prints rewritten as the function
    /// call (see the class remarks). Null stays null; text with none of them is returned as it is.
    /// </summary>
    public static string? FunctionSyntax(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Hints.Any(h => text.Contains(h, StringComparison.Ordinal))) return text;
        var s = text;
        for (var round = 0; round < 1000; round++)
        {
            var next = RewriteOne(s);
            if (next is null) return s;
            s = next;
        }
        return s;
    }

    /// <summary>The text with the first SQL-standard form rewritten, or null when there is none it can read.</summary>
    private static string? RewriteOne(string s)
    {
        var toks = Tokens(s);
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Kind != K.Word) continue;
            string? replacement = null;
            int from = 0, to = 0;
            if (IsWord(s, t, "AT") && AtTimeZone(s, toks, k) is var (open, close, x, z) && open >= 0)
            {
                replacement = $"timezone({z}, {x})";
                (from, to) = (toks[open].Start, toks[close].End);
            }
            else if (Text(s, t) is "SUBSTRING" or "POSITION" or "TRIM" or "OVERLAY" && k + 1 < toks.Count && Is(s, toks[k + 1], '(') &&
                     (PrevSolid(toks, k - 1) is var p && (p < 0 || !(Is(s, toks[p], '.') || toks[p].Kind is K.Word or K.Quoted && !IsKeywordBefore(s, toks[p])))) &&
                     Closing(s, toks, k + 1) is var end && end > 0 && Standard(s, toks, Text(s, t), k + 2, end) is { } call)
            {
                replacement = call;
                (from, to) = (t.Start, toks[end].End);
            }
            if (replacement is not null) return s[..from] + replacement + s[to..];
        }
        return null;
    }

    /// <summary>A word before SUBSTRING( and the like that is an operator keyword, not a name the call would belong to.</summary>
    private static bool IsKeywordBefore(string s, Tok t) =>
        Text(s, t) is "AND" or "OR" or "NOT" or "SELECT" or "WHERE" or "WHEN" or "THEN" or "ELSE" or "CASE" or "AS" or "IS" or "IN"
            or "BY" or "ON" or "LIKE" or "ILIKE" or "BETWEEN" or "DISTINCT" or "FROM" or "VALUES" or "CHECK" or "DEFAULT" or "USING" or "RETURN"
            or "HAVING" or "ALL" or "ANY" or "SOME" or "END";

    /// <summary>
    /// "(X AT TIME ZONE Z)" around the AT at <paramref name="at"/>: the bracket tokens and both operands, or open -1.
    /// A newer server always brackets the whole form itself, so a bracket that belongs to a call is not this.
    /// </summary>
    private static (int Open, int Close, string X, string Z) AtTimeZone(string s, List<Tok> toks, int at)
    {
        var none = (-1, -1, "", "");
        if (at + 4 >= toks.Count || toks[at + 1].Kind != K.Space || !IsWord(s, toks[at + 2], "TIME") ||
            toks[at + 3].Kind != K.Space || !IsWord(s, toks[at + 4], "ZONE"))
            return none;
        if (at < 1 || toks[at - 1].Kind != K.Space) return none;
        // the bracket that is open where AT stands
        var depth = 0;
        var open = -1;
        for (var k = at - 1; k >= 0; k--)
        {
            if (Is(s, toks[k], ')')) depth++;
            else if (Is(s, toks[k], '('))
            {
                if (depth == 0) { open = k; break; }
                depth--;
            }
        }
        if (open < 0) return none;
        var before = PrevSolid(toks, open - 1);
        if (before >= 0 && toks[before].Kind is K.Word or K.Quoted && !IsKeywordBefore(s, toks[before])) return none;
        var close = Closing(s, toks, open);
        if (close < 0) return none;
        // nothing else at this level: "(X AT TIME ZONE Z)" exactly
        for (var k = open + 1; k < close; k++)
        {
            if (Is(s, toks[k], '(')) { k = Closing(s, toks, k); if (k < 0) return none; continue; }
            if (k != at && IsWord(s, toks[k], "AT") && k + 2 < close && IsWord(s, toks[k + 2], "TIME")) return none;
            if (Is(s, toks[k], ',')) return none;
        }
        var x = s[toks[open].End..toks[at].Start].Trim();
        var z = s[toks[at + 4].End..toks[close].Start].Trim();
        return x.Length == 0 || z.Length == 0 ? none : (open, close, x, z);
    }

    /// <summary>The operands of SUBSTRING / POSITION / TRIM / OVERLAY between tokens first..end (exclusive) as a call, or null.</summary>
    private static string? Standard(string s, List<Tok> toks, string name, int first, int end)
    {
        // the inner text split at this level's keyword tokens: (keyword before it, text)
        var parts = new List<(string? Keyword, string Text)>();
        string? keyword = null;
        var from = toks[first < end ? first : end].Start;
        for (var k = first; k < end; k++)
        {
            var t = toks[k];
            if (Is(s, t, '(')) { k = Closing(s, toks, k); if (k < 0 || k >= end) return null; continue; }
            if (Is(s, t, ',')) return null;
            if (t.Kind == K.Word && Text(s, t) is "FROM" or "FOR" or "IN" or "PLACING" or "BOTH" or "LEADING" or "TRAILING" or "SIMILAR" or "ESCAPE")
            {
                parts.Add((keyword, s[from..t.Start].Trim()));
                keyword = Text(s, t);
                from = t.End;
            }
        }
        parts.Add((keyword, s[from..toks[end].Start].Trim()));
        var shape = string.Join(" ", parts.Select(p => (p.Keyword ?? "") + (p.Text.Length > 0 ? ":x" : ":")));

        static string Unwrap(string x)
        {
            var t = x.Trim();
            if (t.Length < 2 || t[0] != '(' || t[^1] != ')') return t;
            var toks = Tokens(t);
            return Closing(t, toks, 0) == toks.Count - 1 ? t[1..^1].Trim() : t;
        }
        string P(int i) => parts[i].Text;

        return name switch
        {
            "SUBSTRING" when shape == ":x FROM:x" => $"\"substring\"({P(0)}, {P(1)})",
            "SUBSTRING" when shape == ":x FROM:x FOR:x" => $"\"substring\"({P(0)}, {P(1)}, {P(2)})",
            "POSITION" when shape == ":x IN:x" => $"\"position\"({Unwrap(P(1))}, {Unwrap(P(0))})",
            "OVERLAY" when shape == ":x PLACING:x FROM:x" => $"\"overlay\"({P(0)}, {P(1)}, {P(2)})",
            "OVERLAY" when shape == ":x PLACING:x FROM:x FOR:x" => $"\"overlay\"({P(0)}, {P(1)}, {P(2)}, {P(3)})",
            "TRIM" when parts.Count == 3 && parts[0].Text.Length == 0 && parts[1].Keyword is { } side && TrimFunction(side) is { } fn &&
                        parts[2].Keyword == "FROM" && P(2).Length > 0 =>
                P(1).Length > 0 ? $"{fn}({P(2)}, {P(1)})" : $"{fn}({P(2)})",
            _ => null
        };
    }

    private static string? TrimFunction(string side) => side switch { "BOTH" => "btrim", "LEADING" => "ltrim", "TRAILING" => "rtrim", _ => null };

    // ------------------------------------------------------------------ parameters

    /// <summary>
    /// pg_get_function_arguments' text without the IN mode, which is the default: "IN a integer, INOUT b integer" reads
    /// "a integer, INOUT b integer", as PostgreSQL 13 prints it. Defaults are put through <see cref="FunctionSyntax"/>.
    /// </summary>
    public static string Arguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return arguments;
        var toks = Tokens(arguments);
        var pieces = new List<string>();
        var from = 0;
        for (var k = 0; k < toks.Count; k++)
        {
            if (Is(arguments, toks[k], '(')) { var c = Closing(arguments, toks, k); if (c < 0) return arguments; k = c; continue; }
            if (!Is(arguments, toks[k], ',')) continue;
            pieces.Add(arguments[from..toks[k].Start]);
            from = toks[k].End;
        }
        pieces.Add(arguments[from..]);
        var cleaned = pieces.Select(p =>
        {
            var t = p.Trim();
            return t.StartsWith("IN ", StringComparison.Ordinal) ? t[3..].TrimStart() : t;
        });
        return FunctionSyntax(string.Join(", ", cleaned))!;
    }

    // ------------------------------------------------------------------ views before PostgreSQL 16

    private static readonly HashSet<string> ClauseWords = new(StringComparer.Ordinal)
    {
        "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR"
    };

    /// <summary>
    /// A view definition from a server older than 16 as 16 prints it: when the query's FROM is exactly one relation
    /// (or one bracketed subquery with its alias) and the query is no UNION, INTERSECT or EXCEPT, the prefix naming
    /// that relation is left off every column at the query's own level ("SELECT t.id FROM s.t t" reads "SELECT id FROM
    /// s.t t"). Subqueries keep theirs, as 16 keeps them. Anything else is returned unchanged.
    /// </summary>
    public static string UnqualifiedSingleRelation(string viewdef)
    {
        if (string.IsNullOrEmpty(viewdef)) return viewdef;
        var s = viewdef;
        var toks = Tokens(s);
        var first = NextSolid(toks, 0);
        if (first < 0 || !IsWord(s, toks[first], "SELECT")) return viewdef;

        // the top level's FROM, and that nothing joins or combines at the top level
        var fromAt = -1;
        var fromEnd = toks.Count;
        for (var k = first; k < toks.Count; k++)
        {
            var t = toks[k];
            if (Is(s, t, '(')) { k = Closing(s, toks, k); if (k < 0) return viewdef; continue; }
            if (t.Kind != K.Word) continue;
            var w = Text(s, t);
            if (w is "UNION" or "INTERSECT" or "EXCEPT") return viewdef;
            if (fromAt < 0 && w == "FROM") { fromAt = k; continue; }
            if (fromAt >= 0 && fromEnd == toks.Count && ClauseWords.Contains(w)) fromEnd = k;
        }
        if (fromAt < 0) return viewdef;
        // a trailing semicolon ends the FROM item too
        var last = PrevSolid(toks, fromEnd - 1);
        if (last > fromAt && Is(s, toks[last], ';')) fromEnd = last;

        var item = Enumerable.Range(fromAt + 1, Math.Max(0, fromEnd - fromAt - 1)).Where(k => toks[k].Kind != K.Space).ToList();
        string? refName = null;
        var p = 0;
        if (item.Count > 0 && Is(s, toks[item[0]], '('))
        {
            // ( subquery ) alias
            var close = Closing(s, toks, item[0]);
            if (close < 0) return viewdef;
            var rest = item.Where(k => k > close).ToList();
            if (rest.Count != 1 || toks[rest[0]].Kind is not (K.Word or K.Quoted)) return viewdef;
            refName = Text(s, toks[rest[0]]);
        }
        else
        {
            if (p < item.Count && IsWord(s, toks[item[p]], "ONLY")) p++;
            if (p >= item.Count || toks[item[p]].Kind is not (K.Word or K.Quoted)) return viewdef;
            var name = Text(s, toks[item[p]]);
            p++;
            if (p + 1 < item.Count && Is(s, toks[item[p]], '.') && toks[item[p + 1]].Kind is K.Word or K.Quoted)
            {
                name = Text(s, toks[item[p + 1]]);
                p += 2;
            }
            if (p < item.Count && toks[item[p]].Kind is K.Word or K.Quoted && !IsWord(s, toks[item[p]], "AS")) { refName = Text(s, toks[item[p]]); p++; }
            else refName = name;
            if (p != item.Count) return viewdef;   // anything more (a column alias list, TABLESAMPLE, ...) is not one plain relation
        }

        // drop "ref." at the query's own level: outside every bracketed subquery, and never inside the FROM item
        var sb = new StringBuilder(s.Length);
        var stack = new Stack<bool>();
        var copied = 0;
        for (var k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (Is(s, t, '('))
            {
                var nx = NextSolid(toks, k + 1);
                stack.Push(nx >= 0 && toks[nx].Kind == K.Word && Text(s, toks[nx]) is "SELECT" or "WITH" or "VALUES" or "TABLE");
                continue;
            }
            if (Is(s, t, ')')) { if (stack.Count > 0) stack.Pop(); continue; }
            if (stack.Contains(true) || (k > fromAt && k < fromEnd)) continue;
            if (t.Kind is not (K.Word or K.Quoted) || Text(s, t) != refName) continue;
            if (k + 2 >= toks.Count || !Is(s, toks[k + 1], '.') || toks[k + 2].Kind is not (K.Word or K.Quoted)) continue;
            var before = k > 0 ? toks[k - 1] : default;
            if (k > 0 && (Is(s, before, '.') || Is(s, before, ':') || before.Kind is K.Word or K.Quoted)) continue;
            // a call in a schema of the same name keeps it
            if (k + 3 < toks.Count && Is(s, toks[k + 3], '(')) continue;
            sb.Append(s, copied, t.Start - copied);
            copied = toks[k + 1].End;
        }
        sb.Append(s, copied, s.Length - copied);
        return sb.ToString();
    }
}
