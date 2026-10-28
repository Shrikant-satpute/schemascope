using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.PostgreSql;

/// <summary>
/// Reads a PostgreSQL database's schema from pg_catalog in two round trips, then hashes it with the PostgreSQL
/// dialect. Everything runs in one READ ONLY, REPEATABLE READ transaction with a statement timeout, so the reader can
/// neither change the database nor hold it up, and every query sees the same moment.
///
/// PostgreSQL 13 or later. The catalog keeps names as PostgreSQL folded them, so objects are keyed exactly
/// (<see cref="IdentifierFold.Exact"/>) and routines with their argument types: "Users" and users, or f(integer) and
/// f(text), are separate objects and never collide.
/// </summary>
public sealed partial class PgSchemaReader : ISchemaReader
{
    /// <summary>The oldest server the catalog queries are written for.</summary>
    public const int MinimumVersionNum = 130000;

    public int CommandTimeoutSeconds { get; init; } = 180;

    private static readonly PgDialect Dialect = new();

    public async Task<SchemaSnapshot> ReadAsync(SchemaSource source, CompareOptions options,
        IProgress<ReadProgress>? progress = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var snapshot = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql,
            Label = source.DisplayName,
            Server = source.Server,
            Database = source.Database
        };

        progress?.Report(new ReadProgress(source.Id, "Connecting", 0));
        PgOpenConnection open;
        try
        {
            open = await PgConnectionService.OpenAsync(source.ConnectionString, ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or ArgumentException)
        {
            throw new SchemaReadException($"Could not connect to {source.DisplayName}: {PgConnectionService.MessageOf(ex)}",
                PgConnectionService.HintFor(ex));
        }
        await using var _ = open;
        var conn = open.Connection;

        var connectMs = total.ElapsedMilliseconds;
        if (string.IsNullOrEmpty(snapshot.Server)) snapshot.Server = conn.Host ?? "";
        snapshot.Port = conn.Port;
        snapshot.Database = conn.Database;

        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            // read only, bounded, and every name in the deparse schema-qualified the same way on every server
            await using (var setup = new NpgsqlCommand(SessionSetup(CommandTimeoutSeconds), conn, tx))
                await setup.ExecuteNonQueryAsync(ct);

            progress?.Report(new ReadProgress(source.Id, "Reading server info", 5));
            var stage = Stopwatch.StartNew();
            await ReadServerInfoAsync(conn, tx, snapshot, ct);
            snapshot.TimingsMs["serverInfo"] = stage.ElapsedMilliseconds;

            var versionNum = int.Parse(snapshot.EngineFacts!["server_version_num"]);
            if (versionNum < MinimumVersionNum)
                throw new SchemaReadException(
                    $"{source.DisplayName} runs PostgreSQL {snapshot.ProductVersion}; SchemaScope reads PostgreSQL 13 and later.");

            progress?.Report(new ReadProgress(source.Id, "Reading catalog", 15));
            stage.Restart();
            var raw = await ReadCatalogAsync(conn, tx, versionNum, ct);
            snapshot.TimingsMs["catalog"] = stage.ElapsedMilliseconds;
            await tx.RollbackAsync(ct);

            progress?.Report(new ReadProgress(source.Id, "Building model", 70));
            stage.Restart();
            // PostgreSQL's own excluded schemas in place of SQL Server's defaults, and names matched exactly
            snapshot.Objects = Assemble(raw, options.ForEngine(Dialect.DefaultExcludedSchemas, exactNames: true));
            snapshot.FetchedBodies = snapshot.Objects.Count(o => o.Type.IsModule());
            snapshot.TimingsMs["assemble"] = stage.ElapsedMilliseconds;
        }
        catch (PostgresException ex)
        {
            throw new SchemaReadException($"Could not read {source.DisplayName}: {PgConnectionService.MessageOf(ex)}",
                PgConnectionService.HintFor(ex));
        }

        progress?.Report(new ReadProgress(source.Id, "Hashing", 85));
        var hash = Stopwatch.StartNew();
        new SchemaNormalizer(options, Dialect).Normalize(snapshot);
        snapshot.TimingsMs["hash"] = hash.ElapsedMilliseconds;
        snapshot.TimingsMs["connect"] = connectMs;
        snapshot.TimingsMs["total"] = total.ElapsedMilliseconds;

        progress?.Report(new ReadProgress(source.Id, "Done", 100,
            $"{snapshot.Objects.Count} objects in {total.ElapsedMilliseconds} ms"));
        return snapshot;
    }

    /// <summary>
    /// The first statement of the reader's transaction: READ ONLY (the server refuses any write in it), a statement
    /// timeout and a lock timeout (so it can neither run forever nor queue behind a DDL lock for long), and search_path
    /// pg_catalog alone, so the deparse names every user object with its schema. JIT off: the catalog joins are
    /// estimated costly enough to be compiled, which takes longer (a fifth of a second a database) than they run.
    /// </summary>
    internal static string SessionSetup(int timeoutSeconds) =>
        $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {timeoutSeconds * 1000}; " +
        "SET LOCAL lock_timeout = 5000; SET LOCAL search_path = pg_catalog; SET LOCAL jit = off;";

    private async Task ReadServerInfoAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SchemaSnapshot s, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(PgCatalogQueries.ServerInfo, conn, tx) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return;
        var version = r.GetString(0);
        var num = r.GetInt32(1);
        s.ProductVersion = version;
        s.MajorVersion = num / 10000;
        s.EngineVersion = $"PostgreSQL {version}";
        s.Edition = "PostgreSQL";
        s.Database = r.GetString(2);
        s.Collation = r.IsDBNull(3) ? "" : r.GetString(3);
        s.EngineFacts = new Dictionary<string, string> { ["server_version_num"] = num.ToString() };
    }

    // ------------------------------------------------------------------ raw catalog rows

    private sealed record Rel(uint Oid, string Schema, string Name, char Kind, string? ViewDef, char Persistence,
        bool RowSecurity, bool ForceRowSecurity, string[] Options, string? PartitionKey, bool IsPartition,
        string? PartitionBound, string[] Parents, string? OfType, char ReplicaIdentity, string? ReplicaIndex, string? ForeignServer,
        string[] ForeignOptions, bool Populated);
    private sealed record Policy(uint Rel, string Name, bool Permissive, char Command, string[] Roles, string? Using,
        string? WithCheck);
    private sealed record Col(uint Rel, int Num, string Name, string Type, bool NotNull, string Identity, string Generated,
        string? Default, string? Collation, long? SeqStart, long? SeqIncrement, long? SeqMin, long? SeqMax, long? SeqCache, bool? SeqCycle,
        bool NotNullNotValid, string[] FdwOptions);
    private sealed record Con(uint Rel, uint TypeOid, string Name, char Kind, string Definition, bool Validated,
        string[] Columns, string? RefSchema, string? RefTable, string[] RefColumns, string DeleteAction, string UpdateAction, string[]? IndexOptions);
    private sealed record Idx(uint Rel, string Name, string Method, bool Unique, string Definition, string? Predicate,
        int KeyCount, string[] Columns, bool?[] Descending);
    private sealed record Routine(uint Oid, string Schema, string Name, char Kind, bool ReturnsSet, string Signature,
        string Definition, string Arguments, string? Result);
    private sealed record Trg(string Schema, string Table, string Name, string Definition, char Enabled, uint Oid);
    private sealed record Seq(string Schema, string Name, string Type, long Start, long Increment, long Min, long Max,
        long Cache, bool Cycle, string? OwnedBy, uint Oid);
    /// <summary>One pg_depend row resolved (<see cref="PgCatalogQueries"/>): kinds c relation, t type, p routine, g trigger, r operator, l collation, o operator class, f text search configuration, y text search dictionary.</summary>
    private sealed record Dep(char Kind, uint Oid, char RefKind, uint RefOid, string? RefColumn, string Via, string? Extension, string? Index);
    private sealed record Typ(uint Oid, string Schema, string Name, char Kind, string BaseType, bool NotNull, string? Default,
        string? Collation, string[] Labels, uint RelOid, string? Subtype, string? SubtypeOpClass, string? RangeCollation,
        string? Canonical, string? SubtypeDiff, string? Multirange);
    private sealed record Coll(uint Oid, string Schema, string Name, char Provider, bool Deterministic, string? Collate, string? Ctype,
        string? Locale, string? Rules);
    private sealed record Rule(uint Rel, string Name, string Definition, char Enabled);
    private sealed record Oper(uint Oid, string Schema, string Name, string? Left, string? Right, string Function, string? Commutator,
        string? Negator, string? Restrict, string? Join, bool Hashes, bool Merges);

    private sealed record TsDict(uint Oid, string Schema, string Name, string Template, string? Options, bool UserTemplate);
    private sealed record TsConfig(uint Oid, string Schema, string Name, string Parser, bool UserParser, string[] Mappings, uint[] Dicts);
    private sealed record OpClass(uint Oid, string Schema, string Name, bool IsDefault, string Type, string Method, string? Family,
        string? Storage, string[] Operators, string[] Functions, uint[] OperatorOids, uint[] FunctionOids, uint TypeOid);
    private sealed record CastRow(uint Oid, string Schema, string Source, string Target, char Method, string? Function, char Context,
        uint FunctionOid, uint SourceOid, uint TargetOid);
    private sealed record Stat(uint Rel, string Schema, string Name, string Definition, int? Target);

    private sealed class RawCatalog
    {
        public List<TsDict> TextSearchDictionaries { get; } = [];
        public List<TsConfig> TextSearchConfigurations { get; } = [];
        public List<OpClass> OperatorClasses { get; } = [];
        public List<CastRow> Casts { get; } = [];
        public List<Stat> Statistics { get; } = [];
        public List<(string Name, string Owner)> Schemas { get; } = [];
        public List<Rel> Relations { get; } = [];
        public List<Col> Columns { get; } = [];
        public List<Con> Constraints { get; } = [];
        public List<Idx> Indexes { get; } = [];
        public List<Routine> Routines { get; } = [];
        public List<Trg> Triggers { get; } = [];
        public List<Seq> Sequences { get; } = [];
        public List<Typ> Types { get; } = [];
        public List<(string Name, string Version, string Schema)> Extensions { get; } = [];
        public List<Policy> Policies { get; } = [];
        public List<Coll> Collations { get; } = [];
        public List<Rule> Rules { get; } = [];
        public List<Oper> Operators { get; } = [];
        public List<Dep> Dependencies { get; } = [];
    }

    private async Task<RawCatalog> ReadCatalogAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int versionNum, CancellationToken ct)
    {
        var raw = new RawCatalog();
        await using var cmd = new NpgsqlCommand(PgCatalogQueries.Catalog(versionNum), conn, tx) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);

        static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static char Ch(NpgsqlDataReader r, int i) => r.IsDBNull(i) || r.GetString(i).Length == 0 ? '\0' : r.GetString(i)[0];
        // the server's deparse of an expression, spelled as every version spells it (PgDeparse)
        static string? Expr(NpgsqlDataReader r, int i) => PgDeparse.FunctionSyntax(Str(r, i));

        while (await r.ReadAsync(ct)) raw.Schemas.Add((r.GetString(0), r.GetString(1)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Relations.Add(new Rel(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Ch(r, 3), ViewText(Expr(r, 4), versionNum),
                Ch(r, 5), r.GetBoolean(6), r.GetBoolean(7), r.GetFieldValue<string[]>(8), Expr(r, 9), r.GetBoolean(10), Expr(r, 11),
                r.GetFieldValue<string[]>(12), Str(r, 13), Ch(r, 14), Str(r, 15), Str(r, 16), r.GetFieldValue<string[]>(17), r.GetBoolean(18)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Columns.Add(new Col(r.GetFieldValue<uint>(0), r.GetInt16(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                Str(r, 5) ?? "", Str(r, 6) ?? "", Expr(r, 7), Str(r, 8),
                r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11),
                r.IsDBNull(12) ? null : r.GetInt64(12), r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetBoolean(14),
                r.GetBoolean(15), r.GetFieldValue<string[]>(16)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Constraints.Add(new Con(r.GetFieldValue<uint>(0), r.GetFieldValue<uint>(1), r.GetString(2), Ch(r, 3), Expr(r, 4)!,
                r.GetBoolean(5), r.GetFieldValue<string[]>(6), Str(r, 7), Str(r, 8), r.GetFieldValue<string[]>(9),
                Str(r, 10) ?? "a", Str(r, 11) ?? "a", r.IsDBNull(12) ? null : r.GetFieldValue<string[]>(12)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Indexes.Add(new Idx(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), Expr(r, 4)!,
                Expr(r, 5), r.GetInt16(6), [.. r.GetFieldValue<string[]>(7).Select(c => PgDeparse.FunctionSyntax(c)!)],
                r.GetFieldValue<bool?[]>(8)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // the IN mode PostgreSQL 14 writes out, and the defaults, as every version spells them - in the header too
            var arguments = Str(r, 7) ?? "";
            var plain = PgDeparse.Arguments(arguments);
            var definition = Str(r, 6) ?? "";
            if (plain != arguments && definition.IndexOf($"({arguments})", StringComparison.Ordinal) is var at and >= 0)
                definition = definition[..at] + $"({plain})" + definition[(at + arguments.Length + 2)..];
            raw.Routines.Add(new Routine(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Ch(r, 3), r.GetBoolean(4),
                r.GetString(5), definition, plain, Str(r, 8)));
        }

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Triggers.Add(new Trg(r.GetString(0), r.GetString(1), r.GetString(2), Expr(r, 3)!, Ch(r, 4), r.GetFieldValue<uint>(5)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Sequences.Add(new Seq(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                r.GetInt64(6), r.GetInt64(7), r.GetBoolean(8), Str(r, 9), r.GetFieldValue<uint>(10)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Types.Add(new Typ(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Ch(r, 3), r.GetString(4), r.GetBoolean(5),
                Expr(r, 6), Str(r, 7), r.GetFieldValue<string[]>(8), r.GetFieldValue<uint>(9), Str(r, 10), Str(r, 11), Str(r, 12),
                Str(r, 13), Str(r, 14), Str(r, 15)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct)) raw.Extensions.Add((r.GetString(0), r.GetString(1), r.GetString(2)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Policies.Add(new Policy(r.GetFieldValue<uint>(0), r.GetString(1), r.GetBoolean(2), Ch(r, 3),
                r.GetFieldValue<string[]>(4), Expr(r, 5), Expr(r, 6)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Collations.Add(new Coll(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Ch(r, 3), r.GetBoolean(4), Str(r, 5),
                Str(r, 6), Str(r, 7), Str(r, 8)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Rules.Add(new Rule(r.GetFieldValue<uint>(0), r.GetString(1), Expr(r, 2)!, Ch(r, 3)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Operators.Add(new Oper(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), r.GetString(5),
                Str(r, 6), Str(r, 7), Str(r, 8), Str(r, 9), r.GetBoolean(10), r.GetBoolean(11)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Dependencies.Add(new Dep(Ch(r, 0), r.GetFieldValue<uint>(1), Ch(r, 2), r.GetFieldValue<uint>(3), Str(r, 4),
                r.GetString(5), Str(r, 6), Str(r, 7)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.TextSearchDictionaries.Add(new TsDict(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetBoolean(5)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.TextSearchConfigurations.Add(new TsConfig(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                r.GetFieldValue<string[]>(5), r.GetFieldValue<uint[]>(6)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.OperatorClasses.Add(new OpClass(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetString(5),
                Str(r, 6), Str(r, 7), r.GetFieldValue<string[]>(8), r.GetFieldValue<string[]>(9), r.GetFieldValue<uint[]>(10),
                r.GetFieldValue<uint[]>(11), r.GetFieldValue<uint>(12)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Casts.Add(new CastRow(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), r.GetString(3), Ch(r, 4), Str(r, 5), Ch(r, 6),
                r.GetFieldValue<uint>(7), r.GetFieldValue<uint>(8), r.GetFieldValue<uint>(9)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Statistics.Add(new Stat(r.GetFieldValue<uint>(0), r.GetString(1), r.GetString(2), Expr(r, 3)!, r.IsDBNull(4) ? null : r.GetInt32(4)));

        return raw;
    }

    /// <summary>A view's text as PostgreSQL 16 prints it (<see cref="PgDeparse.UnqualifiedSingleRelation"/>), from any version.</summary>
    private static string? ViewText(string? viewdef, int versionNum) =>
        viewdef is null || versionNum >= 160000 ? viewdef : PgDeparse.UnqualifiedSingleRelation(viewdef);

    // ------------------------------------------------------------------ model

    private static DbObject New(DbObjectType type, string schema, string name) =>
        new() { Type = type, Schema = schema, Name = name, Fold = IdentifierFold.Exact };

    private static List<DbObject> Assemble(RawCatalog raw, CompareOptions options)
    {
        var objects = new List<DbObject>();
        // by oid, for the dependencies: relations (and a composite type's relation), types, routines, triggers
        var byRel = new Dictionary<uint, DbObject>();
        var byType = new Dictionary<uint, DbObject>();
        var byProc = new Dictionary<uint, DbObject>();
        var byTrigger = new Dictionary<uint, DbObject>();
        var byCollation = new Dictionary<uint, DbObject>();

        foreach (var (name, owner) in raw.Schemas)
        {
            if (options.IsExcluded(DbObjectType.Schema, name, name)) continue;
            var o = New(DbObjectType.Schema, name, name);
            // empty: the public schema with the owner its version gives it
            if (owner.Length > 0) o.Properties["owner"] = owner;
            objects.Add(o);
        }

        var columns = raw.Columns.ToLookup(c => c.Rel);
        var constraints = raw.Constraints.ToLookup(c => c.Rel);
        var typeConstraints = raw.Constraints.Where(c => c.TypeOid != 0).ToLookup(c => c.TypeOid);
        var indexes = raw.Indexes.ToLookup(i => i.Rel);
        var policies = raw.Policies.ToLookup(p => p.Rel);
        var rules = raw.Rules.ToLookup(r => r.Rel);

        foreach (var rel in raw.Relations)
        {
            var type = rel.Kind switch
            {
                'v' => DbObjectType.View, 'm' => DbObjectType.MaterializedView, 'f' => DbObjectType.ForeignTable, _ => DbObjectType.Table
            };
            if (options.IsExcluded(type, rel.Schema, rel.Name)) continue;
            var o = New(type, rel.Schema, rel.Name);

            if (type == DbObjectType.ForeignTable)
            {
                // its columns (with their own options), defaults and checks, its server and options, where it hangs
                o.Columns = [.. columns[rel.Oid].Select(Column)];
                foreach (var c in columns[rel.Oid].Where(c => c.Default is not null && c.Generated == ""))
                    o.Constraints.Add(new ConstraintInfo { Kind = ConstraintKind.Default, ColumnName = c.Name, Definition = c.Default!, IsSystemNamed = true });
                foreach (var con in constraints[rel.Oid].Where(c => c.Kind == 'c')) AddConstraint(o, con);
                foreach (var c in columns[rel.Oid].Where(c => c.FdwOptions.Length > 0))
                    o.Properties[PgCanonicalWriter.ColumnOptionsPrefix + c.Name] = FdwOptions(c.FdwOptions);
                o.Properties["server"] = rel.ForeignServer ?? "";
                if (rel.ForeignOptions.Length > 0) o.Properties["options"] = FdwOptions(rel.ForeignOptions);
                if (rel.IsPartition && rel.Parents.Length > 0)
                {
                    o.Properties["partitionOf"] = rel.Parents[0];
                    o.Properties["partitionBound"] = rel.PartitionBound ?? "";
                }
                else if (rel.Parents.Length > 0) o.Properties["inherits"] = string.Join(", ", rel.Parents);
            }
            else if (type == DbObjectType.Table)
            {
                o.Columns = [.. columns[rel.Oid].Select(Column)];
                foreach (var c in columns[rel.Oid].Where(c => c.Default is not null && c.Generated == ""))
                    o.Constraints.Add(new ConstraintInfo { Kind = ConstraintKind.Default, ColumnName = c.Name, Definition = c.Default!, IsSystemNamed = true });
                foreach (var con in constraints[rel.Oid]) AddConstraint(o, con);
                o.Indexes.AddRange(indexes[rel.Oid].Select(Index));
                TableFacts(o, rel, policies[rel.Oid], options);
                foreach (var rule in rules[rel.Oid]) o.Properties[PgCanonicalWriter.RulePrefix + rule.Name] = RuleText(rule, rel);
            }
            else
            {
                // a view's WITH CHECK OPTION is kept in its options, not in pg_get_viewdef: written out the way pg_dump does
                var checkOption = rel.Options.FirstOrDefault(x => x.StartsWith("check_option=", StringComparison.Ordinal));
                var storage = StorageOptions(rel.Options, options).Where(x => x != checkOption).ToList();
                var body = (rel.ViewDef ?? "").TrimEnd();
                if (checkOption is not null)
                    body = $"{body.TrimEnd(';').TrimEnd()}\n  WITH {checkOption["check_option=".Length..].ToUpperInvariant()} CHECK OPTION;";
                // a materialized view nobody has filled (REFRESH) yet is created as one: its query is not run
                if (type == DbObjectType.MaterializedView && !rel.Populated) body = $"{body.TrimEnd(';').TrimEnd()}\nWITH NO DATA;";

                var sb = new StringBuilder();
                sb.Append(type == DbObjectType.View ? "CREATE OR REPLACE VIEW " : "CREATE MATERIALIZED VIEW ")
                  .Append(PgCanonicalWriter.Q(rel.Schema)).Append('.').Append(PgCanonicalWriter.Q(rel.Name))
                  .Append(storage.Count > 0 ? $" WITH ({string.Join(", ", storage)})" : "").Append(" AS\n")
                  .Append(body);
                // a materialized view's indexes are part of what it is
                foreach (var ix in indexes[rel.Oid].OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    sb.Append('\n').Append(ix.Definition).Append(';');
                    o.Indexes.Add(Index(ix));
                }
                // a view's own rules (DO INSTEAD on an updatable view) are part of what it is too, replaced with it
                foreach (var rule in rules[rel.Oid].OrderBy(x => x.Name, StringComparer.Ordinal))
                    sb.Append('\n').Append(RuleText(rule, rel, orReplace: true));
                // what an INSERT through the view puts in a column it leaves out (ALTER VIEW ... SET DEFAULT), in column order
                foreach (var c in columns[rel.Oid].Where(c => type == DbObjectType.View && c.Default is not null))
                    sb.Append('\n').Append(ViewDefault(rel.Schema, rel.Name, c.Name)).Append(c.Default).Append(';');
                o.Definition = sb.ToString();
                // what the view returns, one column a line, in order: CREATE OR REPLACE VIEW may only add columns at the end
                o.Properties["columns"] = string.Join("\n", columns[rel.Oid].Select(c => $"{PgCanonicalWriter.Q(c.Name)} {c.Type}"));
            }
            objects.Add(o);
            byRel[rel.Oid] = o;
        }

        foreach (var p in raw.Routines)
        {
            var type = p.Kind switch
            {
                'p' => DbObjectType.StoredProcedure,
                'a' => DbObjectType.AggregateFunction,
                _ => p.ReturnsSet ? DbObjectType.TableValuedFunction : DbObjectType.ScalarFunction
            };
            if (options.IsExcluded(type, p.Schema, p.Name)) continue;
            var o = New(type, p.Schema, p.Name);
            o.Signature = p.Signature;
            o.Definition = p.Definition;
            o.Properties["arguments"] = p.Arguments;
            if (p.Result is not null) o.Properties["result"] = p.Result;
            objects.Add(o);
            byProc[p.Oid] = o;
        }

        foreach (var t in raw.Triggers)
        {
            if (options.IsExcluded(DbObjectType.Trigger, t.Schema, t.Name)) continue;
            var o = New(DbObjectType.Trigger, t.Schema, t.Name);
            // trigger names are unique per table only
            o.Signature = $"ON {t.Table}";
            o.ParentName = t.Table;
            o.Definition = t.Enabled == 'D'
                ? $"{t.Definition};\nALTER TABLE {PgCanonicalWriter.Q(t.Schema)}.{PgCanonicalWriter.Q(t.Table)} DISABLE TRIGGER {PgCanonicalWriter.Q(t.Name)};"
                : t.Definition + ";";
            objects.Add(o);
            byTrigger[t.Oid] = o;
        }

        foreach (var s in raw.Sequences)
        {
            if (options.IsExcluded(DbObjectType.Sequence, s.Schema, s.Name)) continue;
            var o = New(DbObjectType.Sequence, s.Schema, s.Name);
            o.Properties["type"] = s.Type;
            o.Properties["startValue"] = s.Start.ToString();
            o.Properties["increment"] = s.Increment.ToString();
            o.Properties["minValue"] = s.Min.ToString();
            o.Properties["maxValue"] = s.Max.ToString();
            o.Properties["cacheSize"] = s.Cache.ToString();
            o.Properties["isCycling"] = s.Cycle ? "1" : "0";
            if (s.OwnedBy is not null) o.Properties["ownedBy"] = s.OwnedBy;
            objects.Add(o);
            byRel[s.Oid] = o;
        }

        foreach (var c in raw.Collations)
        {
            if (options.IsExcluded(DbObjectType.Collation, c.Schema, c.Name)) continue;
            var o = New(DbObjectType.Collation, c.Schema, c.Name);
            o.Properties["provider"] = c.Provider switch { 'i' => "icu", 'b' => "builtin", 'd' => "default", _ => "libc" };
            if (c.Provider == 'c')
            {
                o.Properties["lc_collate"] = c.Collate ?? "";
                o.Properties["lc_ctype"] = c.Ctype ?? "";
            }
            else o.Properties["locale"] = c.Locale ?? c.Collate ?? "";
            if (!c.Deterministic) o.Properties["deterministic"] = "false";
            if (c.Rules is not null) o.Properties["rules"] = c.Rules;
            objects.Add(o);
            byCollation[c.Oid] = o;
        }

        foreach (var t in raw.Types)
        {
            var type = t.Kind switch
            {
                'd' => DbObjectType.Domain, 'e' => DbObjectType.EnumType, 'r' => DbObjectType.RangeType, _ => DbObjectType.CompositeType
            };
            if (options.IsExcluded(type, t.Schema, t.Name)) continue;
            var o = New(type, t.Schema, t.Name);
            switch (type)
            {
                case DbObjectType.Domain:
                    o.Properties["baseType"] = t.BaseType;
                    o.Properties["notNull"] = t.NotNull ? "1" : "0";
                    if (t.Default is not null) o.Properties["default"] = t.Default;
                    if (t.Collation is not null) o.Properties["collation"] = t.Collation;
                    foreach (var con in typeConstraints[t.Oid]) AddConstraint(o, con);
                    break;
                case DbObjectType.EnumType:
                    o.Properties["labels"] = string.Join(", ", t.Labels);
                    break;
                case DbObjectType.RangeType:
                    o.Properties["subtype"] = t.Subtype ?? "";
                    if (t.SubtypeOpClass is not null) o.Properties["subtypeOpClass"] = t.SubtypeOpClass;
                    if (t.RangeCollation is not null) o.Properties["collation"] = t.RangeCollation;
                    if (t.Canonical is not null) o.Properties["canonical"] = t.Canonical;
                    if (t.SubtypeDiff is not null) o.Properties["subtypeDiff"] = t.SubtypeDiff;
                    // the multirange's name only when it is not the one PostgreSQL 14 gives it by itself
                    if (t.Multirange is { } multi && multi != DefaultMultirangeName(t.Name)) o.Properties["multirange"] = multi;
                    break;
                default:
                    o.Columns = [.. columns[t.RelOid].Select(Column)];
                    byRel[t.RelOid] = o;
                    break;
            }
            objects.Add(o);
            byType[t.Oid] = o;
        }

        var byOperator = new Dictionary<uint, DbObject>();
        foreach (var op in raw.Operators)
        {
            if (options.IsExcluded(DbObjectType.Operator, op.Schema, op.Name)) continue;
            var o = New(DbObjectType.Operator, op.Schema, op.Name);
            // operators are keyed on their argument types, NONE for the missing side of a prefix operator
            o.Signature = $"{op.Left ?? "NONE"}, {op.Right ?? "NONE"}";
            o.Properties["function"] = op.Function;
            if (op.Left is not null) o.Properties["leftArg"] = op.Left;
            if (op.Right is not null) o.Properties["rightArg"] = op.Right;
            if (op.Commutator is not null) o.Properties["commutator"] = op.Commutator;
            if (op.Negator is not null) o.Properties["negator"] = op.Negator;
            if (op.Restrict is not null) o.Properties["restrict"] = op.Restrict;
            if (op.Join is not null) o.Properties["join"] = op.Join;
            if (op.Hashes) o.Properties["hashes"] = "1";
            if (op.Merges) o.Properties["merges"] = "1";
            objects.Add(o);
            byOperator[op.Oid] = o;
        }

        foreach (var (name, version, schema) in raw.Extensions)
        {
            if (options.IsExcluded(DbObjectType.Extension, name, name)) continue;
            var o = New(DbObjectType.Extension, name, name);
            o.Properties["version"] = version;
            o.Properties["schema"] = schema;
            objects.Add(o);
        }

        // text search dictionaries and configurations; a template or parser of the user's own is C code, noted so the
        // scripts hold what needs it (needs is what SchemaScope does not create)
        var byTsDict = new Dictionary<uint, DbObject>();
        foreach (var d in raw.TextSearchDictionaries)
        {
            if (options.IsExcluded(DbObjectType.TextSearchDictionary, d.Schema, d.Name)) continue;
            var o = New(DbObjectType.TextSearchDictionary, d.Schema, d.Name);
            o.Properties["template"] = d.Template;
            if (d.Options is { Length: > 0 } init) o.Properties["options"] = init;
            if (d.UserTemplate) o.Properties[PgCanonicalWriter.NeedsPrefix + "template"] = $"text search template {d.Template}";
            objects.Add(o);
            byTsDict[d.Oid] = o;
        }
        var byTsConfig = new Dictionary<uint, DbObject>();
        foreach (var c in raw.TextSearchConfigurations)
        {
            if (options.IsExcluded(DbObjectType.TextSearchConfiguration, c.Schema, c.Name)) continue;
            var o = New(DbObjectType.TextSearchConfiguration, c.Schema, c.Name);
            o.Properties["parser"] = c.Parser;
            if (c.UserParser) o.Properties[PgCanonicalWriter.NeedsPrefix + "parser"] = $"text search parser {c.Parser}";
            foreach (var m in c.Mappings)
            {
                var at = m.IndexOf(" WITH ", StringComparison.Ordinal);
                o.Properties[PgCanonicalWriter.MappingPrefix + m[..at]] = m[(at + " WITH ".Length)..];
            }
            foreach (var dict in c.Dicts) Need(o, byTsDict.GetValueOrDefault(dict));
            objects.Add(o);
            byTsConfig[c.Oid] = o;
        }

        // operator classes, keyed on their access method as PostgreSQL keys them; they need their operators, support
        // functions and type
        var byOpclass = new Dictionary<uint, DbObject>();
        foreach (var c in raw.OperatorClasses)
        {
            if (options.IsExcluded(DbObjectType.OperatorClass, c.Schema, c.Name)) continue;
            var o = New(DbObjectType.OperatorClass, c.Schema, c.Name);
            o.Signature = c.Method;
            if (c.IsDefault) o.Properties["default"] = "1";
            o.Properties["type"] = c.Type;
            o.Properties["method"] = c.Method;
            if (c.Family is not null) o.Properties["family"] = c.Family;
            if (c.Storage is not null) o.Properties["storage"] = c.Storage;
            o.Properties["items"] = string.Join("\n", c.Operators.Concat(c.Functions));
            foreach (var op in c.OperatorOids) Need(o, byOperator.GetValueOrDefault(op));
            foreach (var fn in c.FunctionOids) Need(o, byProc.GetValueOrDefault(fn));
            Need(o, byType.GetValueOrDefault(c.TypeOid));
            objects.Add(o);
            byOpclass[c.Oid] = o;
        }

        // casts, named by their types; they need their function and types
        var casts = new List<(DbObject Cast, DbObject? Function)>();
        foreach (var c in raw.Casts)
        {
            var name = $"{c.Source} AS {c.Target}";
            if (options.IsExcluded(DbObjectType.Cast, c.Schema, name)) continue;
            var o = New(DbObjectType.Cast, c.Schema, name);
            o.Properties["source"] = c.Source;
            o.Properties["target"] = c.Target;
            o.Properties["method"] = c.Method switch { 'i' => "INOUT", 'b' => "BINARY", _ => "FUNCTION" };
            if (c.Function is not null) o.Properties["function"] = c.Function;
            o.Properties["context"] = c.Context switch { 'a' => "ASSIGNMENT", 'i' => "IMPLICIT", _ => "EXPLICIT" };
            var function = byProc.GetValueOrDefault(c.FunctionOid);
            Need(o, function);
            Need(o, byType.GetValueOrDefault(c.SourceOid));
            Need(o, byType.GetValueOrDefault(c.TargetOid));
            objects.Add(o);
            casts.Add((o, function));
        }

        // extended statistics belong to their table, as its policies and rules do
        foreach (var st in raw.Statistics)
            if (byRel.GetValueOrDefault(st.Rel) is { Type: DbObjectType.Table } table)
                table.Properties[PgCanonicalWriter.StatisticsPrefix + $"{PgCanonicalWriter.Q(st.Schema)}.{PgCanonicalWriter.Q(st.Name)}"] = st.Definition.TrimEnd().TrimEnd(';') + ";" +
                    (st.Target is { } target ? $"\nALTER STATISTICS {PgCanonicalWriter.Q(st.Schema)}.{PgCanonicalWriter.Q(st.Name)} SET STATISTICS {target};" : "");

        AddDependencies(raw.Dependencies, byRel, byType, byProc, byTrigger, byCollation, byOperator, byTsConfig, byTsDict, byOpclass);

        // pg_depend records an expression's need of a cast's function, never of the cast itself, while the deparse
        // writes an implicit or assignment cast as the bare value ('5'::numeric): whatever needs such a function may
        // need its cast, so it needs both
        foreach (var (cast, function) in casts.Where(c => c.Function is not null))
            foreach (var o in objects.Where(o => !ReferenceEquals(o, cast)))
                foreach (var d in (o.DependsOn ?? []).Where(d => d.Key == function!.Key).ToList())
                    if (!o.DependsOn!.Contains(d with { Key = cast.Key })) o.DependsOn.Add(d with { Key = cast.Key });
        return objects;
    }

    /// <summary><paramref name="o"/> needs <paramref name="needed"/> (when it was read: a built-in one never is).</summary>
    private static void Need(DbObject o, DbObject? needed)
    {
        if (needed is null || ReferenceEquals(needed, o)) return;
        var dep = new DbDependency(needed.Key, null, "relation", null);
        o.DependsOn ??= [];
        if (!o.DependsOn.Contains(dep)) o.DependsOn.Add(dep);
    }

    /// <summary>
    /// A rule as pg_get_ruledef writes it, with its enabled state when that is not the default. A view's is written as
    /// CREATE OR REPLACE RULE, so the view's text replaces it along with the view.
    /// </summary>
    private static string RuleText(Rule rule, Rel rel, bool orReplace = false)
    {
        var text = rule.Definition.TrimEnd();
        if (orReplace && text.StartsWith("CREATE RULE ", StringComparison.Ordinal)) text = "CREATE OR REPLACE RULE " + text["CREATE RULE ".Length..];
        if (!text.EndsWith(';')) text += ";";
        var state = rule.Enabled switch { 'D' => "DISABLE", 'R' => "ENABLE REPLICA", 'A' => "ENABLE ALWAYS", _ => null };
        return state is null ? text
            : $"{text}\nALTER TABLE {PgCanonicalWriter.Q(rel.Schema)}.{PgCanonicalWriter.Q(rel.Name)} {state} RULE {PgCanonicalWriter.Q(rule.Name)};";
    }

    /// <summary>The start of a view column's default as the view's text carries it: ALTER VIEW "s"."v" ALTER COLUMN "c" SET DEFAULT .</summary>
    public static string ViewDefault(string schema, string view, string column) =>
        $"ALTER VIEW {PgCanonicalWriter.Q(schema)}.{PgCanonicalWriter.Q(view)} ALTER COLUMN {PgCanonicalWriter.Q(column)} SET DEFAULT ";

    /// <summary>The multirange name PostgreSQL 14 makes for a range: its first "range" becomes "multirange", else "_multirange" is added.</summary>
    internal static string DefaultMultirangeName(string range)
    {
        var at = range.IndexOf("range", StringComparison.Ordinal);
        return at >= 0 ? range[..at] + "multirange" + range[(at + "range".Length)..] : range + "_multirange";
    }

    /// <summary>
    /// <see cref="DbObject.DependsOn"/> from pg_depend: what each listed object needs among the listed objects (or the
    /// extension that made it). A sequence's ownership by a column is not a need (the column needs the sequence), and an
    /// object needing itself is kept only with a column - a generated column or a trigger naming one of its own table's.
    /// </summary>
    private static void AddDependencies(List<Dep> deps, Dictionary<uint, DbObject> byRel, Dictionary<uint, DbObject> byType,
        Dictionary<uint, DbObject> byProc, Dictionary<uint, DbObject> byTrigger, Dictionary<uint, DbObject> byCollation,
        Dictionary<uint, DbObject> byOperator, Dictionary<uint, DbObject> byTsConfig, Dictionary<uint, DbObject> byTsDict,
        Dictionary<uint, DbObject> byOpclass)
    {
        // an operator class or operator an extension made is never listed itself: only the extension it belongs to is a need
        DbObject? Find(char kind, uint oid) => kind switch
        {
            'c' => byRel.GetValueOrDefault(oid),
            't' => byType.GetValueOrDefault(oid),
            'p' => byProc.GetValueOrDefault(oid),
            'g' => byTrigger.GetValueOrDefault(oid),
            'l' => byCollation.GetValueOrDefault(oid),
            'r' => byOperator.GetValueOrDefault(oid),
            'f' => byTsConfig.GetValueOrDefault(oid),
            'y' => byTsDict.GetValueOrDefault(oid),
            'o' => byOpclass.GetValueOrDefault(oid),
            _ => null
        };

        foreach (var d in deps)
        {
            if (Find(d.Kind, d.Oid) is not { } dependent) continue;
            string key;
            if (d.Extension is { } ext) key = New(DbObjectType.Extension, ext, ext).Key;
            else if (Find(d.RefKind, d.RefOid) is { } needed)
            {
                if (dependent.Type == DbObjectType.Sequence && needed.Type == DbObjectType.Table) continue;   // OWNED BY
                if (ReferenceEquals(needed, dependent) && d.RefColumn is null) continue;
                key = needed.Key;
            }
            else continue;

            var dep = new DbDependency(key, d.RefColumn, d.Via, d.Index);
            dependent.DependsOn ??= [];
            if (!dependent.DependsOn.Contains(dep)) dependent.DependsOn.Add(dep);
        }
    }

    /// <summary>
    /// What a table is beyond its columns and keys, as <see cref="PgCanonicalWriter"/> writes it: UNLOGGED, the partition
    /// key, the parent and bounds of a partition, plain inheritance, the type of a typed table, WITH (...) options, row level security (and FORCE)
    /// and each policy. Only what is set is recorded, so a plain table carries none of these.
    /// </summary>
    /// <summary>A foreign table's or column's options ("name=value" each) as OPTIONS (...) takes them: name 'value', in name order.</summary>
    private static string FdwOptions(string[] options) => string.Join(", ", options
        .Select(x => x.Split('=', 2)).OrderBy(x => x[0], StringComparer.Ordinal)
        .Select(x => $"{PgCanonicalWriter.Q(x[0])} '{(x.Length > 1 ? x[1] : "").Replace("'", "''")}'"));

    private static void TableFacts(DbObject o, Rel rel, IEnumerable<Policy> policies, CompareOptions options)
    {
        if (rel.Persistence == 'u') o.Properties["persistence"] = "UNLOGGED";
        if (rel.PartitionKey is not null) o.Properties["partitionBy"] = rel.PartitionKey;
        if (rel.IsPartition && rel.Parents.Length > 0)
        {
            o.Properties["partitionOf"] = rel.Parents[0];
            o.Properties["partitionBound"] = rel.PartitionBound ?? "";
        }
        else if (rel.Parents.Length > 0)
        {
            o.Properties["inherits"] = string.Join(", ", rel.Parents);
        }
        if (rel.OfType is not null) o.Properties["ofType"] = rel.OfType;
        // every option as the table has it, fillfactor too: "Ignore fill factor" hides it from the compare
        // (PgCanonicalWriter), never from what 03 creates
        if (rel.Options.Order(StringComparer.Ordinal).ToList() is { Count: > 0 } storage) o.Properties["options"] = string.Join(", ", storage);
        if (rel.RowSecurity) o.Properties["rowSecurity"] = "ENABLE";
        if (rel.ForceRowSecurity) o.Properties["forceRowSecurity"] = "FORCE";
        // what logical replication sends of an updated or deleted row, the default (the primary key) left out. USING
        // INDEX whose index is gone sends nothing, as NOTHING does
        var replica = rel.ReplicaIdentity switch
        {
            'f' => "FULL",
            'n' => "NOTHING",
            'i' => rel.ReplicaIndex is { } index ? $"USING INDEX {index}" : "NOTHING",
            _ => null,
        };
        if (replica is not null) o.Properties["replicaIdentity"] = replica;

        foreach (var p in policies)
        {
            var command = p.Command switch { 'r' => "SELECT", 'a' => "INSERT", 'w' => "UPDATE", 'd' => "DELETE", _ => "ALL" };
            var text = new StringBuilder($"CREATE POLICY {PgCanonicalWriter.Q(p.Name)} ON {PgCanonicalWriter.Q(o.Schema)}.{PgCanonicalWriter.Q(o.Name)}");
            text.Append(p.Permissive ? " AS PERMISSIVE" : " AS RESTRICTIVE").Append($" FOR {command}");
            text.Append($" TO {string.Join(", ", p.Roles)}");
            if (p.Using is not null) text.Append($" USING ({p.Using})");
            if (p.WithCheck is not null) text.Append($" WITH CHECK ({p.WithCheck})");
            o.Properties[PgCanonicalWriter.PolicyPrefix + p.Name] = text.Append(';').ToString();
        }
    }

    /// <summary>A relation's WITH (...) options in a fixed order, fillfactor left out when told to ignore it.</summary>
    private static IEnumerable<string> StorageOptions(string[] reloptions, CompareOptions options) =>
        reloptions.Where(x => !(options.IgnoreFillFactor && x.StartsWith("fillfactor=", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);

    private static ColumnInfo Column(Col c) => new()
    {
        Ordinal = c.Num,
        Name = c.Name,
        DataType = c.Type,
        IsNullable = !c.NotNull,
        IsIdentity = c.Identity is "a" or "d",
        IdentityKind = c.Identity switch { "a" => "ALWAYS", "d" => "BY DEFAULT", _ => null },
        IdentitySeed = c.SeqStart?.ToString(),
        IdentityIncrement = c.SeqIncrement?.ToString(),
        IdentityOptions = c.SeqMin is { } min && c.SeqMax is { } max && c.SeqCache is { } cache
            ? $"MINVALUE {min} MAXVALUE {max} CACHE {cache} {(c.SeqCycle == true ? "CYCLE" : "NO CYCLE")}"
            : null,
        // 's' stored; 'v' virtual (PostgreSQL 18)
        IsComputed = c.Generated is "s" or "v",
        IsPersisted = c.Generated == "s",
        ComputedDefinition = c.Generated is "s" or "v" ? c.Default : null,
        Collation = c.Collation,
        NotNullNotValid = c.NotNullNotValid
    };

    private static IndexInfo Index(Idx i) => new()
    {
        Name = i.Name,
        TypeDesc = i.Method,
        IsUnique = i.Unique,
        FilterDefinition = i.Predicate,
        Definition = i.Definition,
        KeyColumns = [.. i.Columns.Take(i.KeyCount).Select((c, n) => new IndexColumnInfo
            { Name = c, Ordinal = n + 1, Descending = n < i.Descending.Length && i.Descending[n] == true })],
        IncludedColumns = [.. i.Columns.Skip(i.KeyCount)]
    };

    private static void AddConstraint(DbObject o, Con c)
    {
        switch (c.Kind)
        {
            case 'p' or 'u' or 'x':
                o.Indexes.Add(new IndexInfo
                {
                    Name = c.Name,
                    TypeDesc = c.Kind == 'x' ? PgCanonicalWriter.ExcludeConstraint : "btree",
                    IsPrimaryKey = c.Kind == 'p',
                    IsUniqueConstraint = c.Kind == 'u',
                    IsUnique = c.Kind != 'x',
                    Definition = WithIndexOptions(c.Definition, c.IndexOptions),
                    KeyColumns = [.. c.Columns.Select((name, n) => new IndexColumnInfo { Name = name, Ordinal = n + 1 })],
                    IsSystemNamed = c.Name == DefaultName(o.Name, c.Columns, c.Kind == 'p' ? "pkey" : c.Kind == 'u' ? "key" : "excl")
                });
                break;
            case 'f':
                o.ForeignKeys.Add(new ForeignKeyInfo
                {
                    Name = c.Name,
                    Columns = [.. c.Columns],
                    ReferencedSchema = c.RefSchema ?? "",
                    ReferencedTable = c.RefTable ?? "",
                    ReferencedColumns = [.. c.RefColumns],
                    DeleteAction = Action(c.DeleteAction),
                    UpdateAction = Action(c.UpdateAction),
                    IsNotTrusted = !c.Validated,
                    Definition = c.Definition
                });
                break;
            case 'c':
                const string notValid = " NOT VALID";
                var def = c.Definition.EndsWith(notValid, StringComparison.Ordinal) ? c.Definition[..^notValid.Length] : c.Definition;
                o.Constraints.Add(new ConstraintInfo
                {
                    Name = c.Name,
                    Kind = ConstraintKind.Check,
                    Definition = def,
                    IsNotTrusted = !c.Validated,
                    IsSystemNamed = c.Name == DefaultName(o.Name, c.Columns, "check")
                });
                break;
        }
    }

    /// <summary>
    /// A primary or unique key's pg_get_constraintdef with its index's options, where CREATE TABLE and ADD CONSTRAINT
    /// take them: after the columns (and INCLUDE), before USING INDEX TABLESPACE and DEFERRABLE. Written the way
    /// pg_get_constraintdef writes an exclusion constraint's: WITH (fillfactor='80').
    /// </summary>
    internal static string WithIndexOptions(string definition, string[]? options)
    {
        if (options is not { Length: > 0 }) return definition;
        var with = " WITH (" + string.Join(", ", options.Select(x => x.Split('=', 2))
            .Select(x => $"{x[0]}='{(x.Length > 1 ? x[1] : "").Replace("'", "''")}'")) + ")";
        var at = KeyTailRx().Match(definition).Index;
        return definition[..at] + with + definition[at..];
    }

    [GeneratedRegex(@"(?: USING INDEX TABLESPACE (?:""(?:[^""]|"""")*""|\S+))?(?: (?:NOT )?DEFERRABLE)?(?: INITIALLY (?:DEFERRED|IMMEDIATE))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyTailRx();

    /// <summary>The name PostgreSQL gives a constraint nobody named: table_pkey, table_col_key, table_col_check.</summary>
    private static string DefaultName(string table, string[] columns, string suffix) =>
        suffix == "pkey" ? $"{table}_pkey" : $"{table}_{string.Join("_", columns)}_{suffix}";

    private static string Action(string code) => code switch
    {
        "r" => "RESTRICT",
        "c" => "CASCADE",
        "n" => "SET_NULL",
        "d" => "SET_DEFAULT",
        _ => "NO_ACTION"
    };
}
