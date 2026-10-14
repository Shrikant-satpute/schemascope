using System.Text.Json.Serialization;

namespace SchemaScope.Api.Services;

/// <summary>What GET/POST/DELETE /api/write-mode answer.</summary>
public sealed record WriteModeState(
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? ExpiresUtc);

/// <summary>
/// "Read &amp; write": the one switch that lets SchemaScope apply scripts to a database. SchemaScope is read-only by
/// default. The switch lives in this process's memory only - never in the store - so every launch starts read-only,
/// and it turns itself off after <see cref="IdleTimeout"/> without activity on the script and apply routes.
/// </summary>
public sealed class WriteMode(TimeProvider? clock = null)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private DateTime? expiresUtc;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public bool Enabled => State.Enabled;

    public WriteModeState State
    {
        get
        {
            lock (gate)
            {
                if (expiresUtc is { } e && e <= Now) expiresUtc = null;
                return new(expiresUtc is not null, expiresUtc);
            }
        }
    }

    public WriteModeState Enable()
    {
        lock (gate) expiresUtc = Now + IdleTimeout;
        return State;
    }

    public WriteModeState Disable()
    {
        lock (gate) expiresUtc = null;
        return State;
    }

    /// <summary>Activity while it is on pushes the idle expiry back. Never turns it on.</summary>
    public void Touch()
    {
        lock (gate)
        {
            if (expiresUtc is { } e && e > Now) expiresUtc = Now + IdleTimeout;
        }
    }
}
