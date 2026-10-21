using SchemaScope.Core.Engines;
using SchemaScope.Core.Storage;

namespace SchemaScope.Api.Services;

/// <summary>
/// Every engine this build carries, and the ones turned on. A preview engine (PostgreSQL in this version) is off
/// until the user switches on "Show preview engines"; while it is off the server treats it as absent - no
/// <c>/api/engines</c> entry, no test, browse, save or compare - so the switch is enforced here, not just hidden in the
/// UI. The choice is kept in the store's settings and takes effect at once.
/// </summary>
public sealed class EngineCatalog
{
    /// <summary>The settings key: "1" when preview engines are on.</summary>
    public const string PreviewSetting = "engines.showPreview";

    private readonly IReadOnlyList<IEngineProvider> _all;
    private readonly SchemaScopeStore _store;
    private readonly Lock _gate = new();
    private EngineRegistry _enabled;

    public EngineCatalog(IEnumerable<IEngineProvider> providers, SchemaScopeStore store)
    {
        _all = [.. providers];
        _store = store;
        PreviewEnabled = ReadSetting(store);
        _enabled = Build(PreviewEnabled);
    }

    /// <summary>The engines that may be used now: every non-preview one, and the preview ones when switched on.</summary>
    public EngineRegistry Enabled => _enabled;

    public bool PreviewEnabled { get; private set; }

    /// <summary>The preview engines this build has, on or off.</summary>
    public IReadOnlyList<IEngineProvider> PreviewEngines => [.. _all.Where(p => p.Capabilities.Preview).OrderBy(p => (int)p.Engine)];

    /// <summary>Whether this build has <paramref name="engine"/> at all, turned on or not.</summary>
    public bool Carries(DatabaseEngine engine) => _all.Any(p => p.Engine == engine);

    public void SetPreview(bool on)
    {
        lock (_gate)
        {
            _store.SetSetting(PreviewSetting, on ? "1" : "0");
            PreviewEnabled = on;
            _enabled = Build(on);
        }
    }

    private EngineRegistry Build(bool preview) => new(_all.Where(p => !p.Capabilities.Preview || preview));

    /// <summary>Off when the store cannot be read: a preview engine is never turned on by accident.</summary>
    private static bool ReadSetting(SchemaScopeStore store)
    {
        try
        {
            return store.GetSetting(PreviewSetting) == "1";
        }
        catch (Exception)
        {
            return false;
        }
    }
}
