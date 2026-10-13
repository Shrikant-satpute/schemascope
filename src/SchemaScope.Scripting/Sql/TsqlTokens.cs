using System.Text;
using System.Text.RegularExpressions;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Scripting;

/// <summary>One token of <see cref="TsqlTokens.Tokens"/>.</summary>
/// <param name="Kind">'L' string literal (N prefix and quotes kept), 'I' identifier (bracket content without [], or a word), 'N' number, 'P' any other character.</param>
/// <param name="Text">The token text.</param>
public readonly record struct SqlToken(char Kind, string Text);

/// <summary>
/// Token-level helpers of generate_formatting.py (08's "is this only formatting?" classifier),
/// plus the normalisers of report.py, check_flow.py and the Davi overwrite generator.
/// All are literal-aware: text inside '...' is never touched by comment stripping.
/// </summary>
public static partial class TsqlTokens
{
    /// <summary>generate_formatting.py KEYWORDS, verbatim (including the original's "eomonment").</summary>
    public static IReadOnlySet<string> Keywords { get; } = new HashSet<string>(("""
        add all alter and any as asc authorization backup begin between break browse bulk by cascade case
        check checkpoint close clustered coalesce collate column commit compute constraint contains
        containstable continue convert create cross current current_date current_time current_timestamp
        current_user cursor database dbcc deallocate declare default delete deny desc disk distinct
        distributed double drop dump else end errlvl escape except exec execute exists exit external
        fetch file fillfactor for foreign freetext freetexttable from full function goto grant group
        having holdlock identity identity_insert identitycol if in index inner insert intersect into is
        join key kill left like lineno load merge national nocheck nonclustered not null nullif of off
        offsets on open opendatasource openquery openrowset openxml option or order outer over percent
        pivot plan precision primary print proc procedure public raiserror read readtext reconfigure
        references replication restore restrict return revert revoke right rollback rowcount rowguidcol
        rule save schema securityaudit select semantickeyphrasetable semanticsimilaritydetailstable
        semanticsimilaritytable session_user set setuser shutdown some statistics system_user table
        tablesample textsize then to top tran transaction trigger truncate try_convert tsequal union
        unique unpivot update updatetext use user values varying view waitfor when where while with
        within writetext returns output nocount try catch throw go cte max min sum avg count cast isnull
        getdate dateadd datediff datepart datename year month day hour minute second len ltrim rtrim trim
        upper lower substring replace charindex concat format round floor ceiling abs iif choose
        row_number rank dense_rank partition rows range preceding following unbounded first last next
        only json xml path auto root openjson value query string_agg string_split nvarchar varchar char
        nchar int bigint smallint tinyint bit decimal numeric float real money date datetime datetime2
        time datetimeoffset uniqueidentifier varbinary binary text ntext image sysdatetime eomonment eomonth
        scope_identity newid object_id db_name error_message error_number error_line error_state
        error_severity xact_state xact_abort quoted_identifier ansi_nulls readonly local static
        read_only forward_only fast_forward dynamic keyset scroll status rowversion timestamp
        """).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>
    /// generate_formatting.py <c>strip</c>: comments removed (<c>--</c> to end of line, nested-aware
    /// <c>/* */</c>, each replaced by one space), string literals kept byte for byte (with <c>''</c>
    /// escapes), then whitespace runs collapsed to one space and the result trimmed.
    /// </summary>
    public static string Strip(string sql) => WsRx().Replace(Scan(sql, LiteralMode.Keep, lower: false, bracketsOpaque: false), " ").Trim();

    /// <summary>generate_formatting.py <c>tokens</c>: <see cref="Strip"/>, then the TOKEN regex. <c>[x]</c> and <c>x</c> give the same token.</summary>
    public static List<SqlToken> Tokens(string sql)
    {
        var output = new List<SqlToken>();
        foreach (Match m in TokenRx().Matches(Strip(sql)))
        {
            if (m.Groups[1].Success) output.Add(new SqlToken('L', m.Groups[1].Value));
            else if (m.Groups[2].Success) output.Add(new SqlToken('I', m.Groups[2].Value[1..^1]));
            else if (m.Groups[3].Success) output.Add(new SqlToken('I', m.Groups[3].Value));
            else if (m.Groups[4].Success) output.Add(new SqlToken('N', m.Groups[4].Value));
            else output.Add(new SqlToken('P', m.Groups[5].Value));
        }
        return output;
    }

    /// <summary>
    /// SchemaScope's own "Same" test (<see cref="TSqlText.Light"/>): BOM, line endings, trailing spaces and
    /// blank lines ignored, a leading ALTER / CREATE OR ALTER read as CREATE. Equal here means the compare
    /// already shows the module as Same, so 08 skips it.
    /// </summary>
    public static string Light(string? sql) => TSqlText.Light(sql);

    /// <summary>
    /// The Davi overwrite generator's <c>norm</c>: literals kept verbatim, comments to one space,
    /// everything else lower-cased, whitespace collapsed, trimmed.
    /// </summary>
    public static string Norm(string sql) => WsRx().Replace(Scan(sql, LiteralMode.Keep, lower: true, bracketsOpaque: false), " ").Trim();

    /// <summary>
    /// report.py <c>strip_and_norm</c> (drifted_modules.md "formatting only" vs "body differs"): like
    /// <see cref="Norm"/>, but a <c>[...]</c> identifier is taken whole (lower-cased, brackets kept), so a
    /// quote or <c>--</c> inside brackets is not mistaken for a literal or comment.
    /// </summary>
    public static string StripAndNorm(string sql) => WsRx().Replace(Scan(sql, LiteralMode.Keep, lower: true, bracketsOpaque: true), " ").Trim();

    /// <summary>
    /// check_flow.py <c>norm</c>, used to search target module bodies: literals become <c>''</c>, comments a
    /// space, everything lower-cased, <c>[</c> and <c>]</c> removed, whitespace collapsed (not trimmed).
    /// </summary>
    public static string FlowNorm(string sql) =>
        WsRx().Replace(Scan(sql, LiteralMode.Empty, lower: true, bracketsOpaque: false).Replace("[", "").Replace("]", ""), " ");

    private enum LiteralMode { Keep, Empty }

    // The single scanner behind every normaliser above (they differ only in these switches).
    private static string Scan(string sql, LiteralMode literals, bool lower, bool bracketsOpaque)
    {
        sql ??= "";
        var output = new StringBuilder(sql.Length);
        int i = 0, n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (c == '\'')
            {
                var j = i + 1;
                while (j < n)
                {
                    if (sql[j] == '\'')
                    {
                        if (j + 1 < n && sql[j + 1] == '\'') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                var endExcl = Math.Min(j + 1, n);            // Python's sql[i:j+1] clamps at the end
                if (literals == LiteralMode.Keep) output.Append(sql, i, endExcl - i);
                else output.Append("''");
                i = j + 1;
            }
            else if (bracketsOpaque && c == '[')
            {
                var j = sql.IndexOf(']', i);
                if (j == -1) j = n - 1;
                output.Append(sql.AsSpan(i, j + 1 - i).ToString().ToLowerInvariant());
                i = j + 1;
            }
            else if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                var j = sql.IndexOf('\n', i);
                i = j == -1 ? n : j;
                output.Append(' ');
            }
            else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                int d = 1, j = i + 2;
                while (j < n && d > 0)
                {
                    if (j + 1 < n && sql[j] == '/' && sql[j + 1] == '*') { d++; j += 2; }
                    else if (j + 1 < n && sql[j] == '*' && sql[j + 1] == '/') { d--; j += 2; }
                    else j++;
                }
                i = j;
                output.Append(' ');
            }
            else
            {
                output.Append(lower ? char.ToLowerInvariant(c) : c);
                i++;
            }
        }
        return output.ToString();
    }

    [GeneratedRegex(@"(N?'(?:[^']|'')*')|(\[[^\]]*\])|([A-Za-z_@#][A-Za-z0-9_@#$]*)|(\d+(?:\.\d+)?)|(\S)")]
    private static partial Regex TokenRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRx();
}
