using System.Text;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// The offline check of a PostgreSQL run, honest about how far it goes (<see cref="ValidationLevel"/>):
///
///   - every .sql file is UTF-8 without a BOM (psql would read a BOM as part of the first statement);
///   - each file is split by <see cref="PgStatementSplitter"/> - the instance apply will use - and every statement goes
///     through libpg_query, the PostgreSQL parser, which must find exactly one statement in it. Without the parser the
///     check is lexical only (no quote, comment or dollar body left open) and says it did not validate;
///   - nothing that cannot run in a transaction or must never run from here: CONCURRENTLY, VACUUM, ALTER SYSTEM,
///     CREATE / DROP DATABASE or TABLESPACE, and no psql backslash command;
///   - each code body is the source's text exactly (it is copied from pg_get_*def). That proves the text, not that
///     it compiles: bodies are never compile-checked here.
/// </summary>
public sealed partial class PgScriptValidator
{
    /// <summary>The splitter the check uses: the same instance the PostgreSQL apply uses.</summary>
    public IStatementSplitter Splitter => PgStatementSplitter.Instance;

    public const string NotCompileChecked =
        "Code bodies (functions, procedures, views, triggers) are copied from the source's pg_get_*def text and are not compile-checked.";

    /// <summary>Checks every written .sql file and writes 99_review\validation.md.</summary>
    public ValidationSummary Validate(IReadOnlyList<WrittenFile> files, ReviewLog review, int sourceMajor, int targetMajor)
    {
        var parse = PgParser.Use;
        var summary = new ValidationSummary
        {
            Level = parse
                ? new ValidationLevel(Parsed: true, ModuleTextProven: false, NotCompileChecked, PgParser.Name)
                : new ValidationLevel(Parsed: false, ModuleTextProven: false,
                    $"Lexical check only - not validated: the PostgreSQL parser is not available ({PgParser.UnavailableReason ?? "switched off"}). " +
                    NotCompileChecked),
        };
        var rows = new List<(string File, int Statements, string Result)>();

        foreach (var f in files.Where(f => f.FullPath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            var name = string.IsNullOrEmpty(f.Folder) ? f.RelativePath : $"{f.Folder}\\{f.RelativePath}";
            var bytes = File.ReadAllBytes(f.FullPath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                summary.Problems.Add($"{name}: starts with a byte order mark, which psql reads as part of the first statement.");
            var text = ScriptWriter.ReadBack(f.FullPath);

            var statements = Splitter.Split(text);
            summary.Batches += statements.Count;
            var fileProblems = new List<string>();

            if (PgStatementSplitter.Unterminated(text) is { } open)
            {
                summary.SyntaxErrors++;
                fileProblems.Add($"{open} at the end of the file");
            }
            foreach (var s in statements)
            {
                if (parse)
                {
                    var (ok, count, error, line) = PgParser.Parse(s.Text);
                    if (!ok)
                    {
                        summary.SyntaxErrors++;
                        fileProblems.Add($"line {s.StartLine + line - 1}: {error}");
                    }
                    else if (count != 1)
                        fileProblems.Add($"line {s.StartLine}: the parser finds {count} statements where the splitter found one - apply would not send it as psql does");
                }
                else if (Balance(s.Text) is { } unbalanced)
                {
                    summary.SyntaxErrors++;
                    fileProblems.Add($"line {s.StartLine}: {unbalanced}");
                }
                foreach (var refused in Refused(s.Text)) fileProblems.Add($"line {s.StartLine}: {refused}");
            }
            if (MetaCommandRx().Match(PgStatementSplitter.CodeOnly(text)) is { Success: true } meta)
                fileProblems.Add($"line {LineOf(text, meta.Index)}: a psql backslash command; scripts here are plain SQL");

            if (f.Kind == WrittenFileKind.RawModule && f.ExpectedModuleText is { } expected)
            {
                summary.ModulesChecked++;
                if (text.Contains(expected, StringComparison.Ordinal)) summary.ModulesExact++;
                else fileProblems.Add("the code body is not the source's text");
            }

            foreach (var p in fileProblems) summary.Problems.Add($"{name}: {p}");
            rows.Add((name, statements.Count, fileProblems.Count == 0 ? "ok" : string.Join("; ", fileProblems)));
        }

        // the syntax errors are counted on their own; Problems keeps every finding with its place
        if (sourceMajor > targetMajor && sourceMajor > 0 && targetMajor > 0)
            review.Warn($"The source runs PostgreSQL {sourceMajor} and the target {targetMajor}: code and definitions copied from the source " +
                        "may use syntax the target does not have yet. The parse uses the newest grammar, so it cannot tell.");
        review.SetDocument("validation.md", Report(summary, rows));
        return summary;
    }

    private static string Report(ValidationSummary s, List<(string File, int Statements, string Result)> rows)
    {
        var sb = new StringBuilder();
        sb.Append("# Validation\n\n");
        var level = s.Level!;
        sb.Append(level.Parsed
            ? $"Every statement was parsed with {level.Parser}, the PostgreSQL parser, after being split the way psql splits it.\n\n"
            : "**Lexical check only - not validated.** No PostgreSQL parser was available, so only quotes, comments, dollar bodies and brackets were checked.\n\n");
        sb.Append($"- Statements: {s.Batches}\n- Syntax errors: {s.SyntaxErrors}\n- Code bodies that are the source's text exactly: {s.ModulesExact} of {s.ModulesChecked}\n");
        sb.Append($"- {NotCompileChecked}\n\n");
        sb.Append(s.Passed ? "Nothing was found.\n\n" : $"**{s.Problems.Count} problem(s) found** - fix or regenerate before running anything.\n\n");
        sb.Append("| File | Statements | Result |\n|---|---:|---|\n");
        foreach (var (file, n, result) in rows) sb.Append($"| {ReviewLog.Cell(file)} | {n} | {ReviewLog.Cell(result)} |\n");
        return sb.ToString();
    }

    /// <summary>What must never run from these scripts (and what cannot run inside a transaction).</summary>
    internal static IEnumerable<string> Refused(string statement)
    {
        var code = PgStatementSplitter.CodeOnly(statement);
        if (ConcurrentlyRx().IsMatch(code)) yield return "CONCURRENTLY cannot run inside a transaction and is never written here";
        if (VacuumRx().IsMatch(code)) yield return "VACUUM is never written here";
        if (AlterSystemRx().IsMatch(code)) yield return "ALTER SYSTEM changes the whole server and is never written here";
        if (DatabaseRx().IsMatch(code)) yield return "creating or dropping a database or tablespace is never written here";
    }

    /// <summary>The lexical fallback: brackets that do not balance outside quotes and comments.</summary>
    private static string? Balance(string statement)
    {
        var depth = 0;
        foreach (var c in PgStatementSplitter.CodeOnly(statement))
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return "a ) without its (";
        }
        return depth > 0 ? "a ( is not closed" : null;
    }

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, Math.Min(index, text.Length)).Count('\n');

    [GeneratedRegex(@"\bCONCURRENTLY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConcurrentlyRx();

    [GeneratedRegex(@"^\s*VACUUM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VacuumRx();

    [GeneratedRegex(@"^\s*ALTER\s+SYSTEM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlterSystemRx();

    [GeneratedRegex(@"^\s*(?:CREATE|DROP)\s+(?:DATABASE|TABLESPACE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseRx();

    [GeneratedRegex(@"(?m)^[ \t]*\\")]
    private static partial Regex MetaCommandRx();
}
