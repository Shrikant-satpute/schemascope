using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Api.Services;

/// <summary>One sign-in method as the connection form offers it.</summary>
/// <param name="Short">The few words a connection list shows ("SQL login", "Entra password").</param>
/// <param name="NeedsUser">The form asks for a user name and password.</param>
public sealed record EngineAuthOption(AuthMode Mode, string Label, string Short, bool NeedsUser);

/// <summary>The connection form's wording and switches for one engine.</summary>
public sealed record EngineFormText(
    string ServerLabel, string ServerHint, string ServerPlaceholder,
    string ConnectionStringHint, string ConnectionStringPlaceholder, string DatabasePlaceholder,
    bool Port, bool SslMode, bool Encrypt, bool TrustServerCertificate, bool ReadOnlyIntent);

/// <summary>What a new connection of this engine starts with.</summary>
public sealed record EngineDefaults(AuthMode Auth, bool Encrypt, bool TrustServerCertificate, int ConnectTimeoutSeconds);

/// <summary>
/// GET /api/engines: one engine as the UI renders it. Capabilities and wording only - never a connection, a host or a
/// secret. The server enforces the capabilities whatever the UI shows.
/// </summary>
/// <param name="ShortName">For engine chips ("SQL", "PG").</param>
public sealed record EngineDescriptor(
    DatabaseEngine Engine, string DisplayName, string ShortName, bool Preview,
    int? DefaultPort, string? BrowseDatabase, string DefaultSchema, IReadOnlyList<string> DefaultExcludedSchemas,
    string MonacoLanguage, string WordSeparators, TransactionSemantics Transactions,
    EngineCapabilities Capabilities, IReadOnlyList<EngineAuthOption> Auth, EngineFormText Form, EngineDefaults Defaults);

/// <summary>Builds <see cref="EngineDescriptor"/>s: the provider's facts plus the form's wording for that engine.</summary>
public static class EngineDescriptors
{
    public static EngineDescriptor Describe(IEngineProvider p)
    {
        var (shortName, auth, form, defaults) = Ui(p.Engine);
        return new EngineDescriptor(p.Engine, p.DisplayName, shortName, p.Capabilities.Preview,
            DatabaseEngines.DefaultPort(p.Engine), p.Connections.BrowseDatabase, p.Dialect.DefaultSchema,
            p.Dialect.DefaultExcludedSchemas, p.Dialect.MonacoLanguage, p.Dialect.WordSeparators, p.Capabilities.Ddl,
            p.Capabilities, auth, form, defaults);
    }

    private static (string Short, IReadOnlyList<EngineAuthOption> Auth, EngineFormText Form, EngineDefaults Defaults) Ui(DatabaseEngine engine) =>
        engine switch
        {
            // the words the form used before it was driven by this, unchanged
            DatabaseEngine.SqlServer => ("SQL",
                [
                    new(AuthMode.Windows, "Windows (current user)", "Windows auth", false),
                    new(AuthMode.SqlLogin, "SQL login", "SQL login", true),
                    new(AuthMode.AzureAdPassword, "Microsoft Entra - password", "Entra password", true),
                    new(AuthMode.AzureAdInteractive, "Microsoft Entra - interactive (MFA)", "Entra interactive", false),
                    new(AuthMode.AzureAdDefault, "Microsoft Entra - default", "Entra default", false),
                ],
                new EngineFormText(
                    ServerLabel: "Server name", ServerHint: @"host, host\instance or host,port",
                    ServerPlaceholder: "localhost  or  10.0.0.5,1433",
                    ConnectionStringHint: "Anything Microsoft.Data.SqlClient accepts.",
                    ConnectionStringPlaceholder: "Server=10.0.0.5,1433;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true",
                    DatabasePlaceholder: "Platform_Core",
                    Port: false, SslMode: false, Encrypt: true, TrustServerCertificate: true, ReadOnlyIntent: true),
                new EngineDefaults(AuthMode.Windows, Encrypt: false, TrustServerCertificate: true, ConnectTimeoutSeconds: 15)),
            DatabaseEngine.PostgreSql => ("PG",
                [
                    new(AuthMode.Password, "User name and password", "Password", true),
                ],
                new EngineFormText(
                    ServerLabel: "Host", ServerHint: "host name or address; several: host1,host2",
                    ServerPlaceholder: "localhost  or  db.internal",
                    ConnectionStringHint: "Anything Npgsql accepts, as key=value pairs (postgres:// URIs are not accepted).",
                    ConnectionStringPlaceholder: "Host=db.internal;Port=5432;Database=app;Username=app;Password=***;SSL Mode=VerifyFull",
                    DatabasePlaceholder: "app",
                    Port: true, SslMode: true, Encrypt: false, TrustServerCertificate: true, ReadOnlyIntent: false),
                // TLS is checked unless the user opts out: Verify full, no trust
                new EngineDefaults(AuthMode.Password, Encrypt: false, TrustServerCertificate: false, ConnectTimeoutSeconds: 15)),
            _ => throw new EngineNotAvailableException(engine)
        };
}
