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
        if (needsRaw) incoming.RawConnectionString = saved.RawConnectionString;
        return true;
    }
}
