using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SchemaScope.Core.Model;

/// <summary>
/// Everything the user can switch on or off before a compare.
/// Defaults are chosen to cut noise without ever hiding a real change:
/// anything ignored here still shows up as "formatting only" rather than vanishing.
/// </summary>
public sealed class CompareOptions : IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>The object types a set of options knows about: 1 for SQL Server's own (Schema..Synonym), 2 with
    /// PostgreSQL's (MaterializedView..Extension).</summary>
    public const int CurrentTypeSet = 2;

    private static readonly DbObjectType[] AddedInTypeSet2 =
        [DbObjectType.MaterializedView, DbObjectType.Domain, DbObjectType.EnumType, DbObjectType.CompositeType, DbObjectType.Extension];

    /// <summary>
    /// Which object types existed when these options were written (<see cref="CurrentTypeSet"/>). Options saved
    /// before a type existed - a profile, a history entry, a request without this field - never chose to leave it
    /// out, so it is included when they are read back, rather than quietly dropped from the compare.
    /// </summary>
    public int TypeSet { get; set; } = CurrentTypeSet;

    // JSON without TypeSet was written before it existed (type set 1); with it, the setter overwrites this
    void IJsonOnDeserializing.OnDeserializing() => TypeSet = 1;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (TypeSet < 2) IncludedTypes.UnionWith(AddedInTypeSet2);
        TypeSet = Math.Max(TypeSet, CurrentTypeSet);
    }

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

    /// <summary>SQL Server's own schemas: the default <see cref="ExcludedSchemas"/>.</summary>
    public static IReadOnlyList<string> SqlServerExcludedSchemas { get; } =
        ["sys", "INFORMATION_SCHEMA", "guest", "db_owner", "db_accessadmin",
         "db_securityadmin", "db_ddladmin", "db_backupoperator", "db_datareader",
         "db_datawriter", "db_denydatareader", "db_denydatawriter"];

    public List<string> ExcludedSchemas { get; set; } = [.. SqlServerExcludedSchemas];

    /// <summary>Wildcard patterns (* and ?) matched against schema.name.</summary>
    public List<string> ExcludedNamePatterns { get; set; } = [];

    public bool IncludeSystemObjects { get; set; }

    /// <summary>
    /// Excluded schemas and name patterns match exactly, for an engine whose names are exact (PostgreSQL: excluding
    /// "App" leaves app alone). Set by <see cref="ForEngine"/>, never stored.
    /// </summary>
    [JsonIgnore]
    public bool ExactNames { get; set; }

    private List<Regex>? _compiledExcludes;

    /// <summary>
    /// These options as an engine other than SQL Server reads with them: SQL Server's default schemas (as the app
    /// wrote them) give way to the engine's own, so a real PostgreSQL schema called sys or guest is compared, and
    /// names match exactly when the engine's do. Schemas the user added stay excluded.
    /// </summary>
    public CompareOptions ForEngine(IEnumerable<string> defaultExcludedSchemas, bool exactNames)
    {
        var copy = Clone();
        copy.ExcludedSchemas = [.. ExcludedSchemas.Where(s => !SqlServerExcludedSchemas.Contains(s, StringComparer.Ordinal))
            .Concat(defaultExcludedSchemas).Distinct(StringComparer.Ordinal)];
        copy.ExactNames = exactNames;
        return copy;
    }

    public bool IsExcluded(DbObjectType type, string schema, string name)
    {
        if (!IncludedTypes.Contains(type)) return true;
        if (ExcludedSchemas.Contains(schema, ExactNames ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)) return true;

        if (ExcludedNamePatterns.Count == 0) return false;

        _compiledExcludes ??= [.. ExcludedNamePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(
                "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                (ExactNames ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant))];

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
        IncludeSystemObjects = IncludeSystemObjects,
        TypeSet = TypeSet,
        ExactNames = ExactNames
    };
}
