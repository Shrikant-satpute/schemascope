using System.Text.RegularExpressions;

namespace SchemaScope.Core.Model;

/// <summary>
/// Everything the user can switch on or off before a compare.
/// Defaults are chosen to cut noise without ever hiding a real change:
/// anything ignored here still shows up as "formatting only" rather than vanishing.
/// </summary>
public sealed class CompareOptions
{
    // ---- text comparison ----
    public bool IgnoreComments { get; set; }
    public bool IgnoreCase { get; set; }

    /// <summary>Re-parse mismatches with ScriptDom and re-print them in one canonical style.</summary>
    public bool UseSmartParseCompare { get; set; } = true;

    // ---- schema detail ----
    public bool IgnoreCollation { get; set; } = true;
    public bool IgnoreFillFactor { get; set; } = true;
    public bool IgnoreIndexPadding { get; set; } = true;
    public bool IgnoreIdentitySeed { get; set; } = true;
    public bool IgnoreSystemNamedConstraints { get; set; } = true;
    public bool IgnoreColumnOrder { get; set; }
    public bool IgnoreNotForReplication { get; set; } = true;
    public bool IgnoreFileGroups { get; set; } = true;

    // ---- scope ----
    public HashSet<DbObjectType> IncludedTypes { get; set; } =
        [.. Enum.GetValues<DbObjectType>()];

    public List<string> ExcludedSchemas { get; set; } =
        ["sys", "INFORMATION_SCHEMA", "guest", "db_owner", "db_accessadmin",
         "db_securityadmin", "db_ddladmin", "db_backupoperator", "db_datareader",
         "db_datawriter", "db_denydatareader", "db_denydatawriter"];

    /// <summary>Wildcard patterns (* and ?) matched against schema.name.</summary>
    public List<string> ExcludedNamePatterns { get; set; } = [];

    public bool IncludeSystemObjects { get; set; }

    private List<Regex>? _compiledExcludes;

    public bool IsExcluded(DbObjectType type, string schema, string name)
    {
        if (!IncludedTypes.Contains(type)) return true;
        if (ExcludedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase)) return true;

        if (ExcludedNamePatterns.Count == 0) return false;

        _compiledExcludes ??= [.. ExcludedNamePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(
                "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))];

        var full = $"{schema}.{name}";
        return _compiledExcludes.Any(rx => rx.IsMatch(full) || rx.IsMatch(name));
    }

    /// <summary>Invalidate the cached wildcard regexes after the patterns change.</summary>
    public void ResetPatternCache() => _compiledExcludes = null;

    public CompareOptions Clone() => new()
    {
        IgnoreComments = IgnoreComments,
        IgnoreCase = IgnoreCase,
        UseSmartParseCompare = UseSmartParseCompare,
        IgnoreCollation = IgnoreCollation,
        IgnoreFillFactor = IgnoreFillFactor,
        IgnoreIndexPadding = IgnoreIndexPadding,
        IgnoreIdentitySeed = IgnoreIdentitySeed,
        IgnoreSystemNamedConstraints = IgnoreSystemNamedConstraints,
        IgnoreColumnOrder = IgnoreColumnOrder,
        IgnoreNotForReplication = IgnoreNotForReplication,
        IgnoreFileGroups = IgnoreFileGroups,
        IncludedTypes = [.. IncludedTypes],
        ExcludedSchemas = [.. ExcludedSchemas],
        ExcludedNamePatterns = [.. ExcludedNamePatterns],
        IncludeSystemObjects = IncludeSystemObjects
    };
}
