using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SchemaScope.Core.Model;

namespace SchemaScope.SqlServer;

/// <summary>
/// Builds connection strings from the friendly form, tests them, and lists the
/// databases on a server. Everything here is read only.
/// </summary>
public sealed class SqlServerConnectionService
{
    public string BuildConnectionString(ConnectionSettings s, string? overrideDatabase = null)
    {
        if (s.UseRawConnectionString && !string.IsNullOrWhiteSpace(s.RawConnectionString))
        {
            var raw = new SqlConnectionStringBuilder(s.RawConnectionString)
            {
                ApplicationName = "SchemaScope"
            };
            if (!string.IsNullOrWhiteSpace(overrideDatabase)) raw.InitialCatalog = overrideDatabase;
            return raw.ConnectionString;
        }

        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.Server,
            InitialCatalog = overrideDatabase ?? s.Database,
            ApplicationName = "SchemaScope",
            ConnectTimeout = s.ConnectTimeoutSeconds <= 0 ? 15 : s.ConnectTimeoutSeconds,
            Encrypt = s.Encrypt,
            TrustServerCertificate = s.TrustServerCertificate,
            MultipleActiveResultSets = false,
            Pooling = true
        };

        if (s.ApplicationIntentReadOnly) b.ApplicationIntent = ApplicationIntent.ReadOnly;

        switch (s.Auth)
        {
            case AuthMode.Windows:
                b.IntegratedSecurity = true;
                break;

            case AuthMode.SqlLogin:
                b.IntegratedSecurity = false;
                b.UserID = s.UserName ?? "";
                b.Password = s.Password ?? "";
                break;

            case AuthMode.AzureAdPassword:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                b.UserID = s.UserName ?? "";
                b.Password = s.Password ?? "";
                break;

            case AuthMode.AzureAdInteractive:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(s.UserName)) b.UserID = s.UserName;
                break;

            case AuthMode.AzureAdDefault:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
        }

        return b.ConnectionString;
    }

    public async Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ConnectionTestResult();

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand($"""
                {CatalogQueries.ServerInfo}

                SELECT COUNT(*)
                FROM sys.objects
                WHERE type IN ('U','V','P','FN','IF','TF','TR') AND is_ms_shipped = 0;

                -- Can this login actually read module bodies? Without VIEW DEFINITION
                -- we would report every procedure as "unavailable", so check up front.
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.sql_modules) THEN 1
                                 WHEN NOT EXISTS (SELECT 1 FROM sys.objects
                                                  WHERE type IN ('P','V','FN','IF','TF','TR')) THEN 1
                                 ELSE 0 END AS bit);
                """, conn)
            { CommandTimeout = 30 };

            await using var r = await cmd.ExecuteReaderAsync(ct);

            if (await r.ReadAsync(ct))
            {
                result.ProductVersion = r.IsDBNull(0) ? null : r.GetString(0);
                result.Edition = r.IsDBNull(1) ? null : r.GetString(1);
                result.DatabaseName = r.IsDBNull(3) ? null : r.GetString(3);
                result.ServerName = r.IsDBNull(4) ? null : r.GetString(4);
                result.Collation = r.IsDBNull(5) ? null : r.GetString(5);
            }

            if (await r.NextResultAsync(ct) && await r.ReadAsync(ct))
                result.ObjectCount = r.GetInt32(0);

            if (await r.NextResultAsync(ct) && await r.ReadAsync(ct))
                result.CanViewDefinitions = r.GetBoolean(0);

            result.Success = true;
            result.ElapsedMs = sw.ElapsedMilliseconds;

            if (!result.CanViewDefinitions)
                result.Hint =
                    "Connected, but this login cannot read procedure and view bodies. " +
                    "Ask a DBA to run:  GRANT VIEW DEFINITION TO [your_login];";

            return result;
        }
        catch (SqlException ex)
        {
            result.Success = false;
            result.Message = ex.Message;
            result.Hint = HintFor(ex);
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }
    }

    public async Task<List<DatabaseListItem>> ListDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        // Connect to master so the list works even before a database is chosen.
        var b = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(b.InitialCatalog)) b.InitialCatalog = "master";

        var list = new List<DatabaseListItem>();

        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(CatalogQueries.ListDatabases, conn) { CommandTimeout = 30 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DatabaseListItem { Name = r.GetString(0), HasAccess = r.GetBoolean(2) });

        return list;
    }

    private static string? HintFor(SqlException ex) => ex.Number switch
    {
        18456 => "Login failed. Check the user name and password.",
        4060 => "That login cannot open this database. Check the database name and the login's access.",
        53 or 40615 => "Server not found or not reachable. Check the name, the port and any firewall.",
        -2 => "Timed out waiting for the server. Try a longer connect timeout.",
        -2146893019 => "The server certificate is not trusted. Tick 'Trust server certificate'.",
        _ => null
    };
}
