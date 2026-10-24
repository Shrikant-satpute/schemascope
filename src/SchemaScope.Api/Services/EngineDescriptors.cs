using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

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

/// <summary>One "Create scripts" section as the dialog shows it for an engine.</summary>
/// <param name="Applicable">False: the engine has no such thing (PostgreSQL's table types); shown greyed out, never sent.</param>
public sealed record EngineScriptSection(ScriptSection Section, string Folder, string Label, string Hint, string? Warning, bool Applicable);

/// <summary>The "Create scripts" dialog's wording for an engine. Null for SQL Server, whose wording is built into the UI.</summary>
/// <param name="Runner">The client the folders' run_all.ps1 call: "psql".</param>
/// <param name="Validation">What the offline check does on this build, in words (it never claims a parse it cannot do).</param>
/// <param name="Parsed">True when that check uses the engine's own parser; false for a lexical check only.</param>
public sealed record EngineScriptText(string Runner, string Validation, bool Parsed, IReadOnlyList<EngineScriptSection> Sections);

/// <summary>The "Apply to target" dialog's wording for an engine. Null for SQL Server, whose wording is built into the UI.</summary>
/// <param name="Transactions">What a file's transaction promises, worded from the engine's <see cref="TransactionSemantics"/>.</param>
/// <param name="TypedNameExact">The typed database name must match letter for letter (the server checks the same rule).</param>
public sealed record EngineApplyText(string Transactions, bool TypedNameExact);

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
    EngineCapabilities Capabilities, IReadOnlyList<EngineAuthOption> Auth, EngineFormText Form, EngineDefaults Defaults,
    EngineScriptText? Scripts = null, EngineApplyText? Apply = null);

/// <summary>Builds <see cref="EngineDescriptor"/>s: the provider's facts plus the form's wording for that engine.</summary>
public static class EngineDescriptors
{
    public static EngineDescriptor Describe(IEngineProvider p)
    {
        var (shortName, auth, form, defaults) = Ui(p.Engine);
        return new EngineDescriptor(p.Engine, p.DisplayName, shortName, p.Capabilities.Preview,
            DatabaseEngines.DefaultPort(p.Engine), p.Connections.BrowseDatabase, p.Dialect.DefaultSchema,
            p.Dialect.DefaultExcludedSchemas, p.Dialect.MonacoLanguage, p.Dialect.WordSeparators, p.Capabilities.Ddl,
            p.Capabilities, auth, form, defaults, ScriptText(p), ApplyText(p));
    }

    /// <summary>The Apply dialog's promise for an engine that has Apply, from its transaction semantics; SQL Server keeps the UI's.</summary>
    private static EngineApplyText? ApplyText(IEngineProvider p)
    {
        if (p.Engine == DatabaseEngine.SqlServer || !p.Capabilities.Features.Contains(EngineFeature.Apply)) return null;
        var transactions = p.Capabilities.Ddl switch
        {
            TransactionSemantics.PerFileTransactional =>
                "Each file runs in its own transaction, and a statement waits at most 5 seconds for a lock. If one fails, that file " +
                "is rolled back and the run stops there - the files before it stay applied. The compare runs again afterwards.",
            _ =>
                $"{p.DisplayName} cannot roll back schema changes. Each file is one statement; if one fails, the files before it stay " +
                "applied. The compare runs again afterwards.",
        };
        return new EngineApplyText(transactions, TypedNameExact: ConnectionRules.TypedNameComparison(p.Engine) == StringComparison.Ordinal);
    }

    /// <summary>PostgreSQL's sections, hints and runner; SQL Server keeps the UI's built-in wording.</summary>
    private static EngineScriptText? ScriptText(IEngineProvider p)
    {
        if (p.Engine != DatabaseEngine.PostgreSql || !p.Capabilities.Features.Contains(EngineFeature.Scripts)) return null;
        var parsed = PgParser.Available;
        return new EngineScriptText("psql",
            parsed
                ? $"Parsed with the PostgreSQL parser, {PgParser.Name}; code bodies are copied from the source and not compile-checked."
                : "Lexical check only - not validated: the PostgreSQL parser could not be loaded on this machine.",
            parsed,
            [
                new(ScriptSection.NewColumns, "01", "New columns",
                    "Adds columns the source has on tables both databases have - only a column that is nullable or has a default.", null, true),
                new(ScriptSection.TableTypes, "02", "Table types", "Not applicable: PostgreSQL has no table types.", null, false),
                new(ScriptSection.MissingObjects, "03", "Missing objects",
                    "Creates the schemas, types, sequences, tables, views, functions and procedures the target lacks, in dependency order. " +
                    "Code is copied from the source and not compile-checked; extensions are only noted.", null, true),
                new(ScriptSection.ColumnTypes, "04", "Column types",
                    "Makes columns bigger or lets them be empty where the source does. Never makes one smaller; held while a view reads the column.",
                    null, true),
                new(ScriptSection.Constraints, "05", "Constraints",
                    "Adds missing defaults, checks, keys and foreign keys, if existing data allows it. Checks and foreign keys go in NOT VALID, then are validated.",
                    null, true),
                new(ScriptSection.Indexes, "06", "Indexes",
                    "Adds the indexes the target does not have, without CONCURRENTLY: writes to a table wait while its index builds.", null, true),
                new(ScriptSection.Triggers, "07", "Triggers",
                    "Creates the triggers the target does not have, with their trigger functions. Never run by RUN.ps1.",
                    "Changes what every write to the table does - needs the module owner's OK.", true),
                new(ScriptSection.Formatting, "08-09", "Formatting",
                    "Re-creates code that differs only in layout or comments from the source's text (08), and renames columns whose names " +
                    "differ only by letter case (09). Never run by RUN.ps1.",
                    "Replaces the target's text of that code, and renames columns that code may name in the old spelling.", true),
                new(ScriptSection.DifferentModules, "10", "Different code",
                    "Replaces functions, procedures, views and triggers whose code really differs, with CREATE OR REPLACE where the signature allows. " +
                    "Never run by RUN.ps1.",
                    "Overwrites the target's own code - a fix made only on the target is lost. Check 99_review first.", true),
            ]);
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
                    ServerLabel: "Host", ServerHint: "host name or address; several: host1,host2 (the port goes in Port)",
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
