namespace SchemaScope.Core.Engines;

/// <summary>
/// The engines this build can talk to, one provider each. The owner app builds it from DI; the share viewer never
/// has one. A SQL Server-only registry for legacy constructors and tests is <c>EngineRegistries.SqlServerOnly()</c>
/// in SchemaScope.SqlServer (Core carries no driver, so it cannot build that provider itself).
/// </summary>
public sealed class EngineRegistry
{
    private readonly Dictionary<DatabaseEngine, IEngineProvider> _providers;

    public EngineRegistry(IEnumerable<IEngineProvider> providers)
    {
        _providers = [];
        foreach (var p in providers)
            if (!_providers.TryAdd(p.Engine, p))
                throw new ArgumentException($"Two providers registered for {p.Engine}.", nameof(providers));
        All = [.. _providers.Values.OrderBy(p => (int)p.Engine)];
    }

    /// <summary>Every provider, in engine order.</summary>
    public IReadOnlyList<IEngineProvider> All { get; }

    /// <exception cref="EngineNotAvailableException">No provider for <paramref name="e"/>.</exception>
    public IEngineProvider Get(DatabaseEngine e) =>
        _providers.TryGetValue(e, out var p) ? p : throw new EngineNotAvailableException(e);

    public bool TryGet(DatabaseEngine e, out IEngineProvider provider) => _providers.TryGetValue(e, out provider!);

    public bool Supports(DatabaseEngine e, EngineFeature f) =>
        _providers.TryGetValue(e, out var p) && p.Capabilities.Features.Contains(f);
}
