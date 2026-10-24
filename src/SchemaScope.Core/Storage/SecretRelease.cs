using System.Data.Common;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Core.Storage;

/// <summary>
/// Decides when a stored secret may be put back into an incoming request. There is one implementation of this
/// rule, here beside the store that applies the same <see cref="ConnectionSettings.SameDestination"/> when saving.
/// </summary>
public static class SecretRelease
{
    public const string Masked = "********";

    /// <summary>
    /// A saved connection comes back from the UI with "********" instead of the
    /// real password. Swap the stored secret in before using it.
    ///
    /// The secret is only ever released for the exact destination it was saved
    /// against. If the caller changed the engine, server, port, login or auth mode,
    /// the password has to be typed again - otherwise anything able to reach the
    /// API could name a saved connection, point it at a host of its own choosing,
    /// and harvest the password from the other end. The same goes for asking for
    /// weaker TLS than the secret was saved with (<see cref="ConnectionSettings.WeakerTls"/>).
    /// </summary>
    public static bool TryRehydrate(ConnectionSettings incoming, SchemaScopeStore store, out string? error)
    {
        error = null;

        // Only a password login has a password to put back. Windows and the
        // interactive Azure modes never stored one, so an edited server there
        // releases nothing and needs nothing typed.
        var needsPassword = incoming.UsesPassword &&
                            (string.IsNullOrEmpty(incoming.Password) || incoming.Password == Masked);
        var needsRaw = incoming.UseRawConnectionString &&
                       (string.IsNullOrEmpty(incoming.RawConnectionString) ||
                        incoming.RawConnectionString.Contains(Masked));

        if (!needsPassword && !needsRaw) return true;
        if (string.IsNullOrWhiteSpace(incoming.Id)) return true;

        var saved = store.GetConnection(incoming.Id);
        if (saved is null) return true;

        if (!ConnectionSettings.SameDestination(incoming, saved))
        {
            error = "The server or login was changed, so the saved password no longer applies. " +
                    "Type the password again.";
            return false;
        }

        if (ConnectionSettings.WeakerTls(incoming, saved))
        {
            error = "The SSL mode is weaker than the one the password was saved with, so the saved password is not sent. " +
                    "Type the password again to connect this way.";
            return false;
        }

        if (needsPassword) incoming.Password = saved.Password;
        if (needsRaw)
        {
            // nothing typed, or the saved string as the UI shows it: the saved string. An edited one keeps its edits and
            // gets only the saved secrets back, for the same login and no weaker TLS - else the password is typed again
            if (string.IsNullOrEmpty(incoming.RawConnectionString) ||
                incoming.RawConnectionString == ConnectionSettings.MaskConnectionString(saved.RawConnectionString ?? ""))
                incoming.RawConnectionString = saved.RawConnectionString;
            else if (MergeSecrets(incoming.Engine, incoming.RawConnectionString, saved.RawConnectionString) is { } merged)
                incoming.RawConnectionString = merged;
            else
            {
                error = "The connection string was changed in a way the saved password does not follow (another login, weaker SSL, or a " +
                        "secret it was not saved with), so it is not sent. Type the password into the string again.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <paramref name="edited"/> with each masked secret replaced by the value <paramref name="saved"/> holds under that
    /// key, when the edit keeps the login and asks for no weaker TLS (the hosts and port were checked by
    /// <see cref="ConnectionSettings.SameDestination"/>). Null when it cannot be read or the secrets may not follow.
    /// </summary>
    internal static string? MergeSecrets(DatabaseEngine engine, string edited, string? saved)
    {
        var e = new DbConnectionStringBuilder();
        var s = new DbConnectionStringBuilder();
        try
        {
            e.ConnectionString = edited;
            s.ConnectionString = saved ?? "";
        }
        catch (ArgumentException)
        {
            return null;
        }

        var comparison = engine == DatabaseEngine.SqlServer ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Value(e, UserKeys) ?? "", Value(s, UserKeys) ?? "", comparison)) return null;
        if (TlsRank(engine, e) is not { } now || TlsRank(engine, s) is not { } before || now < before) return null;

        foreach (var key in e.Keys.Cast<string>().ToList())
        {
            if (!SecretKeys.IsSecret(key) || e[key]?.ToString() != Masked) continue;
            if (!s.TryGetValue(key, out var value) || value?.ToString() is not { Length: > 0 } secret) return null;
            e[key] = secret;
        }
        return e.ConnectionString;
    }

    private static readonly string[] UserKeys = ["username", "user name", "user id", "userid", "uid", "user"];

    private static string? Value(DbConnectionStringBuilder b, string[] keys)
    {
        foreach (var k in keys)
            if (b.TryGetValue(k, out var v) && v?.ToString() is { Length: > 0 } text) return text.Trim();
        return null;
    }

    /// <summary>
    /// How much TLS a raw string asks for, higher is stronger, as each driver reads it (an absent key is the driver's
    /// default); null for a value this cannot read, which then releases nothing.
    /// </summary>
    private static int? TlsRank(DatabaseEngine engine, DbConnectionStringBuilder b)
    {
        static bool? Flag(string? v) => v?.ToLowerInvariant() switch
        {
            null => false, "true" or "yes" or "1" => true, "false" or "no" or "0" => false, _ => null
        };
        if (engine == DatabaseEngine.SqlServer)
        {
            var trust = Flag(Value(b, ["trustservercertificate", "trust server certificate"]));
            int? encrypt = Value(b, ["encrypt"])?.ToLowerInvariant() switch
            {
                null or "true" or "yes" or "mandatory" => 1,
                "false" or "no" or "optional" => 0,
                "strict" => 2,
                _ => null
            };
            if (trust is null || encrypt is null) return null;
            return encrypt switch { 0 => 0, 1 => trust.Value ? 1 : 2, _ => 3 };
        }
        var mode = Value(b, ["ssl mode", "sslmode"])?.Replace("-", "").ToLowerInvariant() switch
        {
            "disable" => 0, "allow" => 1, null or "prefer" => 2, "require" => 3, "verifyca" => 4, "verifyfull" => 5, _ => (int?)null
        };
        var trusted = Flag(Value(b, ["trust server certificate", "trustservercertificate"]));
        if (mode is null || trusted is null) return null;
        return trusted.Value && mode >= 4 ? 3 : mode;
    }
}
