using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaScope.Core.Model;

namespace SchemaScope.Core.Normalization;

/// <summary>
/// Turns raw catalog data into comparable text plus two hashes.
///
///   Hash      - light normalization. Equal hash means the objects are equal.
///   LooseHash - aggressive strip (comments, whitespace, case). Used to tell
///               "only the formatting moved" apart from a real code change.
///
/// Hashing everything up front is what makes the compare fast: 90%+ of objects
/// match on Hash and are never looked at again.
/// </summary>
public sealed class SchemaNormalizer(CompareOptions options)
{
    private readonly CompareOptions _options = options;

    public void Normalize(SchemaSnapshot snapshot)
    {
        Parallel.ForEach(snapshot.Objects, NormalizeObject);
    }

    public void NormalizeObject(DbObject o)
    {
        string compareText;

        if (o.Type.IsModule())
        {
            if (o.IsEncrypted || o.DefinitionUnavailable)
            {
                // No body to compare. Fall back to the signature so at least
                // parameter changes are still visible.
                compareText = SignatureText(o);
            }
            else
            {
                compareText = TSqlText.Light(o.Definition);
                if (_options.IgnoreComments)
                    compareText = TSqlText.Light(TSqlText.StripComments(compareText));
            }
        }
        else
        {
            compareText = CanonicalScriptWriter.Write(o, _options);
        }

        o.CompareText = compareText;
        o.LineCount = TSqlText.CountLines(o.DisplayText);

        var forHash = _options.IgnoreCase ? compareText.ToUpperInvariant() : compareText;
        o.Hash = Sha256(forHash);

        var loose = Loosen(compareText);
        o.LooseHash = Sha256(loose);
        o.LooseLength = loose.Length;
    }

    /// <summary>
    /// Everything a formatting change can touch, removed: comments, whitespace,
    /// bracket quoting and letter case. Dropping the brackets here means
    /// [dbo].[Foo] vs dbo.Foo is caught by a hash compare instead of needing a
    /// full parse, which is the difference between milliseconds and seconds.
    /// </summary>
    private static string Loosen(string text)
    {
        var stripped = TSqlText.StripComments(text);
        var collapsed = TSqlText.CollapseWhitespace(stripped);
        return collapsed.Replace("[", "").Replace("]", "").ToUpperInvariant();
    }

    private static string SignatureText(DbObject o)
    {
        var sb = new StringBuilder();
        sb.Append(o.Type.Label()).Append(' ').Append(o.FullName).Append('\n');
        if (o.IsEncrypted) sb.Append("-- WITH ENCRYPTION: body cannot be read\n");
        if (o.DefinitionUnavailable) sb.Append("-- No VIEW DEFINITION permission: body cannot be read\n");
        foreach (var p in o.Parameters.OrderBy(p => p.Ordinal))
            sb.Append("  ").Append(p.Name).Append(' ').Append(p.DataType)
              .Append(p.IsOutput ? " OUTPUT" : "").Append('\n');
        return sb.ToString();
    }

    private static string Sha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // ---------------------------------------------------------------------
    //  Deep check: parse both sides and print them back in one fixed style.
    //  Only ever called for the handful of objects that fail both hashes,
    //  so the cost does not show up in the total run time.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns true when two module bodies parse to the same tree - meaning the
    /// only difference is layout, bracket quoting or comments.
    /// Returns false if either side fails to parse (we never guess).
    /// </summary>
    public static bool ParsesIdentically(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        var l = CanonicalPrint(left);
        if (l is null) return false;
        var r = CanonicalPrint(right);
        if (r is null) return false;

        return string.Equals(l, r, StringComparison.Ordinal);
    }

    /// <summary>Re-prints T-SQL in one fixed style. Null when the text does not parse.</summary>
    public static string? CanonicalPrint(string sql)
    {
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors is { Count: > 0 } || fragment is null) return null;

            var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
            {
                AlignClauseBodies = false,
                AlignColumnDefinitionFields = false,
                AlignSetClauseItem = false,
                AsKeywordOnOwnLine = false,
                IncludeSemicolons = true,
                IndentSetClause = false,
                IndentViewBody = false,
                KeywordCasing = KeywordCasing.Uppercase,
                MultilineInsertSourcesList = false,
                MultilineInsertTargetsList = false,
                MultilineSelectElementsList = false,
                MultilineSetClauseItems = false,
                MultilineViewColumnsList = false,
                MultilineWherePredicatesList = false,
                NewLineBeforeCloseParenthesisInMultilineList = false,
                NewLineBeforeFromClause = false,
                NewLineBeforeGroupByClause = false,
                NewLineBeforeHavingClause = false,
                NewLineBeforeJoinClause = false,
                NewLineBeforeOffsetClause = false,
                NewLineBeforeOpenParenthesisInMultilineList = false,
                NewLineBeforeOrderByClause = false,
                NewLineBeforeOutputClause = false,
                NewLineBeforeWhereClause = false,
                SqlVersion = SqlVersion.Sql160
            });

            generator.GenerateScript(fragment, out var script);
            return TSqlText.CollapseWhitespace(script).ToUpperInvariant();
        }
        catch
        {
            // A parse or generate failure just means "cannot prove they are the
            // same" - the caller falls back to reporting a real difference.
            return null;
        }
    }
}
