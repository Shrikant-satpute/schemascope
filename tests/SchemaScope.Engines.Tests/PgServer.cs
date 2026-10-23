using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using Testcontainers.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// A test that needs a PostgreSQL container. Skipped unless SCHEMASCOPE_ENGINE_TESTS=1 - checked here, before
/// anything looks for Docker, so a plain "dotnet test" (CI's Windows job, release.yml) skips it in milliseconds.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PgServer.Enabled) Skip = "Engine tests are off: set SCHEMASCOPE_ENGINE_TESTS=1 (needs Docker).";
    }
}

/// <summary>A theory that needs a PostgreSQL container, gated like <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PgServer.Enabled) Skip = "Engine tests are off: set SCHEMASCOPE_ENGINE_TESTS=1 (needs Docker).";
    }
}

/// <summary>
/// Refuses any endpoint a test did not start itself. A container is published on a random host port; the ports the
/// developer's own servers listen on (PostgreSQL 5432, MySQL 3306) on this machine are refused outright, whatever
/// the container setup says, so a test can never reach the user's real data.
/// </summary>
public static class EndpointGuard
{
    private static readonly int[] RealServerPorts = [5432, 3306];

    public static void Check(string host, int port, PgServer startedByUs)
    {
        if (RealServerPorts.Contains(port) && IsThisMachine(host))
            throw new InvalidOperationException($"Refusing {host}:{port}: that is a database server already running on this machine.");
        if (!string.Equals(host, startedByUs.Host, StringComparison.OrdinalIgnoreCase) || port != startedByUs.Port)
            throw new InvalidOperationException($"Refusing {host}:{port}: not the container this test run started.");
    }

    /// <summary>Every host and the port of an Npgsql string, checked as above.</summary>
    public static string Checked(string connectionString, PgServer startedByUs)
    {
        var ep = ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, connectionString)
                 ?? throw new InvalidOperationException("Refusing a connection string whose destination cannot be read.");
        foreach (var host in ep.Hosts) Check(host, ep.Port ?? 5432, startedByUs);
        return connectionString;
    }

    public static bool IsThisMachine(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "." or "(local)" ||
        host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)));
}

/// <summary>
/// One throwaway PostgreSQL container for a test collection, on a random host port. Each test makes its own
/// databases (<see cref="CreateDatabaseAsync"/>), named schemascope_test_&lt;guid&gt;_&lt;part&gt;, and only names of that
/// shape are ever dropped.
/// </summary>
public class PgServer : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("SCHEMASCOPE_ENGINE_TESTS") == "1";

    public const string User = "postgres";
    public string Password { get; } = "pw-" + Guid.NewGuid().ToString("n")[..16];
    public string Host { get; private set; } = "";
    public int Port { get; private set; }

    protected virtual string Image => "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (!Enabled) return;   // the builder below is the first thing that would look for Docker
        _container = new PostgreSqlBuilder(Image)
            .WithUsername(User)
            .WithPassword(Password)
            .WithDatabase("postgres")
            .Build();
        await _container.StartAsync();
        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort);
        EndpointGuard.Check(Host, Port, this);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>A saved-connection form for one database on this container (no TLS: the image has none).</summary>
    public ConnectionSettings Settings(string database, string? user = null, string? password = null) => new()
    {
        Engine = DatabaseEngine.PostgreSql,
        Label = database,
        Server = Host,
        Port = Port,
        Database = database,
        Auth = AuthMode.Password,
        UserName = user ?? User,
        Password = password ?? Password,
        Ssl = Core.Engines.SslMode.Disable,
        // the explicit opt-in Disable needs (no TLS at all)
        TrustServerCertificate = true,
    };

    public string ConnectionString(string database, string? user = null, string? password = null) =>
        EndpointGuard.Checked(new PgConnectionService().BuildConnectionString(Settings(database, user, password)), this);

    public SchemaSource Source(string id, string database, string? user = null, string? password = null) => new()
    {
        Id = id,
        Label = database,
        Engine = DatabaseEngine.PostgreSql,
        Server = Host,
        Database = database,
        ConnectionString = ConnectionString(database, user, password)
    };

    public async Task ExecAsync(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> ExecInContainerAsync(params string[] command)
    {
        var r = await _container!.ExecAsync(command);
        return ((int)(r.ExitCode ?? -1), r.Stdout, r.Stderr);
    }

    /// <summary>Puts a file into this container (the scripts psql then runs inside it).</summary>
    public Task CopyToContainerAsync(byte[] content, string path) => _container!.CopyAsync(content, path);

    private static readonly Regex TestName = new("^schemascope_test_[0-9a-f]{32}_[a-z0-9]+$", RegexOptions.CultureInvariant);

    public static string NewName(string part) => $"schemascope_test_{Guid.NewGuid():n}_{part}";

    public async Task<string> CreateDatabaseAsync(string part)
    {
        var name = NewName(part);
        await ExecAsync("postgres", $"CREATE DATABASE \"{name}\"");
        return name;
    }

    /// <summary>Drops a database this test run made - and refuses any name that is not of that shape.</summary>
    public async Task DropAsync(string name)
    {
        if (!TestName.IsMatch(name)) throw new InvalidOperationException($"Refusing to drop '{name}': not a test database.");
        NpgsqlConnection.ClearAllPools();
        await ExecAsync("postgres", $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    /// <summary>A login role for one test, dropped by <see cref="DropRoleAsync"/> (same name rule).</summary>
    public async Task<(string Name, string Password)> CreateRoleAsync()
    {
        var name = NewName("role");
        var password = "pw-" + Guid.NewGuid().ToString("n")[..12];
        await ExecAsync("postgres", $"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE");
        return (name, password);
    }

    public async Task DropRoleAsync(string name)
    {
        if (!TestName.IsMatch(name)) throw new InvalidOperationException($"Refusing to drop role '{name}': not a test role.");
        await ExecAsync("postgres", $"DROP ROLE IF EXISTS \"{name}\"");
    }
}

/// <summary>PostgreSQL 13, the oldest version the reader supports.</summary>
public sealed class Pg13Server : PgServer
{
    protected override string Image => "postgres:13-alpine";
}

/// <summary>PostgreSQL 18: NOT NULL rows in pg_constraint, virtual generated columns.</summary>
public sealed class Pg18Server : PgServer
{
    protected override string Image => "postgres:18-alpine";
}

[CollectionDefinition(Name)]
public sealed class PgCollection : ICollectionFixture<PgServer>
{
    public const string Name = "postgres 16";
}

[CollectionDefinition(Name)]
public sealed class Pg13Collection : ICollectionFixture<Pg13Server>
{
    public const string Name = "postgres 13";
}

[CollectionDefinition(Name)]
public sealed class Pg18Collection : ICollectionFixture<Pg18Server>
{
    public const string Name = "postgres 18";
}
