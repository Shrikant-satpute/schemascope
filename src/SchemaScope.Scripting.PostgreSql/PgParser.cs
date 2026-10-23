using PgSqlParser;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// The real PostgreSQL parser (libpg_query, through pgsqlparser), when its native library loads. If it cannot load -
/// a platform it has no build for, a blocked temp folder for the single-file exe's native libraries - every caller
/// falls back to the lexical check and says so; nothing ever claims a parse that did not happen.
/// </summary>
public static class PgParser
{
    private static readonly Lazy<(bool Ok, string Name, string? Why)> Probe = new(() =>
    {
        try
        {
            var r = Parser.Parse("SELECT 1");
            if (!r.IsSuccess) return (false, "", $"the parser refused a trivial statement ({r.Error!.Message})");
            var v = r.Value!.Version;   // e.g. 180006
            return (true, $"libpg_query (PostgreSQL {v / 10000}.{v % 100})", null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
                                       or TypeInitializationException or TypeLoadException or InvalidOperationException)
        {
            return (false, "", $"its native library could not be loaded ({ex.GetType().Name})");
        }
    });

    /// <summary>True when statements can be parsed.</summary>
    public static bool Available => Probe.Value.Ok;

    /// <summary>"libpg_query (PostgreSQL 18.6)"; empty when not available.</summary>
    public static string Name => Probe.Value.Name;

    /// <summary>Why the parser is not available; null when it is.</summary>
    public static string? UnavailableReason => Probe.Value.Why;

    /// <summary>Tests only: behave as if the native library could not be loaded (in the calling flow only, so tests running beside it are not affected).</summary>
    internal static bool ForceUnavailable { get => Forced.Value; set => Forced.Value = value; }

    private static readonly AsyncLocal<bool> Forced = new();

    /// <summary>One parse: how many statements it holds, or the error with its 1-based line inside <paramref name="sql"/>.</summary>
    public static (bool Ok, int Statements, string? Error, int Line) Parse(string sql)
    {
        var r = Parser.Parse(sql);
        if (r.IsSuccess) return (true, r.Value!.Stmts.Count, null, 0);
        var at = r.Error!.GetLineAndColumn(sql);
        return (false, 0, r.Error.Message, at?.Item1 ?? 1);
    }

    /// <summary>The parser's own split (text of each statement, without its semicolon), for the parity test.</summary>
    internal static IReadOnlyList<string>? SplitWithParser(string sql)
    {
        var r = Parser.SplitWithParser(sql);
        return r.IsSuccess ? [.. r.Value!.Statements.Select(s => s.Text)] : null;
    }

    /// <summary>Whether to use the parser for this run.</summary>
    internal static bool Use => Available && !ForceUnavailable;
}
