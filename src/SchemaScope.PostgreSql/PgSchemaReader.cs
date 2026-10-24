using System.Data;
using System.Diagnostics;
using System.Text;
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
public sealed class PgSchemaReader : ISchemaReader
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
    /// pg_catalog alone, so the deparse names every user object with its schema.
    /// </summary>
    internal static string SessionSetup(int timeoutSeconds) =>
        $"SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = {timeoutSeconds * 1000}; " +
        "SET LOCAL lock_timeout = 5000; SET LOCAL search_path = pg_catalog;";

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
        string? PartitionBound, string[] Parents);
    private sealed record Policy(uint Rel, string Name, bool Permissive, char Command, string[] Roles, string? Using,
        string? WithCheck);
    private sealed record Col(uint Rel, int Num, string Name, string Type, bool NotNull, string Identity, string Generated,
        string? Default, string? Collation, long? SeqStart, long? SeqIncrement, long? SeqMin, long? SeqMax, long? SeqCache, bool? SeqCycle);
    private sealed record Con(uint Rel, uint TypeOid, string Name, char Kind, string Definition, bool Validated,
        string[] Columns, string? RefSchema, string? RefTable, string[] RefColumns, string DeleteAction, string UpdateAction);
    private sealed record Idx(uint Rel, string Name, string Method, bool Unique, string Definition, string? Predicate,
        int KeyCount, string[] Columns, bool?[] Descending);
    private sealed record Routine(uint Oid, string Schema, string Name, char Kind, bool ReturnsSet, string Signature,
        string Definition, string Arguments, string? Result);
    private sealed record Trg(string Schema, string Table, string Name, string Definition, char Enabled, uint Oid);
    private sealed record Seq(string Schema, string Name, string Type, long Start, long Increment, long Min, long Max,
        long Cache, bool Cycle, string? OwnedBy, uint Oid);
    /// <summary>One pg_depend row resolved (<see cref="PgCatalogQueries"/>): kinds c relation, t type, p routine, g trigger.</summary>
    private sealed record Dep(char Kind, uint Oid, char RefKind, uint RefOid, string? RefColumn, string Via, string? Extension, string? Index);
    private sealed record Typ(uint Oid, string Schema, string Name, char Kind, string BaseType, bool NotNull, string? Default,
        string? Collation, string[] Labels, uint RelOid, string? Subtype, string? SubtypeOpClass, string? RangeCollation,
        string? Canonical, string? SubtypeDiff, string? Multirange);
    private sealed record Coll(uint Oid, string Schema, string Name, char Provider, bool Deterministic, string? Collate, string? Ctype,
        string? Locale, string? Rules);
    private sealed record Rule(uint Rel, string Name, string Definition, char Enabled);

    private sealed class RawCatalog
    {
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
                r.GetFieldValue<string[]>(12)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Columns.Add(new Col(r.GetFieldValue<uint>(0), r.GetInt16(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                Str(r, 5) ?? "", Str(r, 6) ?? "", Expr(r, 7), Str(r, 8),
                r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11),
                r.IsDBNull(12) ? null : r.GetInt64(12), r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetBoolean(14)));

        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Constraints.Add(new Con(r.GetFieldValue<uint>(0), r.GetFieldValue<uint>(1), r.GetString(2), Ch(r, 3), Expr(r, 4)!,
                r.GetBoolean(5), r.GetFieldValue<string[]>(6), Str(r, 7), Str(r, 8), r.GetFieldValue<string[]>(9),
                Str(r, 10) ?? "a", Str(r, 11) ?? "a"));

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
            raw.Dependencies.Add(new Dep(Ch(r, 0), r.GetFieldValue<uint>(1), Ch(r, 2), r.GetFieldValue<uint>(3), Str(r, 4),
                r.GetString(5), Str(r, 6), Str(r, 7)));

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
            var type = rel.Kind switch { 'v' => DbObjectType.View, 'm' => DbObjectType.MaterializedView, _ => DbObjectType.Table };
            if (options.IsExcluded(type, rel.Schema, rel.Name)) continue;
            var o = New(type, rel.Schema, rel.Name);

            if (type == DbObjectType.Table)
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

        foreach (var (name, version, schema) in raw.Extensions)
        {
            if (options.IsExcluded(DbObjectType.Extension, name, name)) continue;
            var o = New(DbObjectType.Extension, name, name);
            o.Properties["version"] = version;
            o.Properties["schema"] = schema;
            objects.Add(o);
        }

        AddDependencies(raw.Dependencies, byRel, byType, byProc, byTrigger, byCollation);
        return objects;
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
        Dictionary<uint, DbObject> byProc, Dictionary<uint, DbObject> byTrigger, Dictionary<uint, DbObject> byCollation)
    {
        // an operator class or operator is never listed itself: only the extension it belongs to is a need
        DbObject? Find(char kind, uint oid) => kind switch
        {
            'c' => byRel.GetValueOrDefault(oid),
            't' => byType.GetValueOrDefault(oid),
            'p' => byProc.GetValueOrDefault(oid),
            'g' => byTrigger.GetValueOrDefault(oid),
            'l' => byCollation.GetValueOrDefault(oid),
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
    /// key, the parent and bounds of a partition, plain inheritance, WITH (...) options, row level security (and FORCE)
    /// and each policy. Only what is set is recorded, so a plain table carries none of these.
    /// </summary>
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
        if (StorageOptions(rel.Options, options).ToList() is { Count: > 0 } storage) o.Properties["options"] = string.Join(", ", storage);
        if (rel.RowSecurity) o.Properties["rowSecurity"] = "ENABLE";
        if (rel.ForceRowSecurity) o.Properties["forceRowSecurity"] = "FORCE";

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
        Collation = c.Collation
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
                    Definition = c.Definition,
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
