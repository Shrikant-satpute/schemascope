# SchemaScope: multi-database plan

Status: **plan only, nothing implemented yet.** Base design: the *risk-first incremental plan*, with ideas taken from the provider-first and canonical-model-first designs where the reviews preferred them. Written 2026-10-21.

---

## 1. Goal and non-goals

### Goal
Support PostgreSQL and MySQL/MariaDB alongside SQL Server, and possibly SQLite and Oracle later. For each engine, every capability SchemaScope has today needs to work correctly in that engine's dialect: **read, compare, create scripts, validate, and Write-mode apply**. On top of that, add **cross-database compare**, for example a SQL Server source against a PostgreSQL target, that reports honestly what matches, what only matches through a type mapping, and what cannot be compared at all.

### Constraints that must not regress
- The 337 existing tests pass **with no assertion edits**. The only changes allowed are constructor overloads in test helpers.
- SQL Server script output stays **byte-identical**. The `DestructiveSectionTests` digests `768F7256…` and `1B16E76C…` plus the new goldens in Phase R0 are the tripwire. Any digest change counts as a regression until it is explained.
- The security model stays intact:
  - stored-secret release only for the exact destination (`TryRehydrate` / `ConnectionSecrets`);
  - loopback API token (`LocalAuth`);
  - Host-header check;
  - CORS only in Debug builds;
  - GET-only share viewer (`ViewerHost`);
  - all Write-mode gates: typed DB name, Protected refused, hash-checked single-use plans, audit written before anything runs.
- Tests **never connect to the local PostgreSQL 16 (5432) or MySQL 8 (3306) Windows services**. They use throwaway Docker containers on random ports only.

### Non-goals (for this plan)
- Translating code between dialects (T-SQL to PL/pgSQL and so on). Module bodies are **never** translated. Across engines they are compared by signature only and are held back in `99_review`.
- Applying cross-engine scripts in-app. Cross-engine scripts are written to a folder only.
- Destructive (`11_destructive`) scripts across engines. They are refused permanently.
- Refactoring the existing ~9.5k-line T-SQL generator. It is wrapped, not moved or rewritten.
- Multi-engine support in QueryScope. It stays SQL Server-only and its exe does not change.
- Oracle and SQLite. They are deferred until someone asks for them (see §14).

---

## 2. Architecture

### 2.1 Guiding rule
**SQL Server code is wrapped, not rewritten.** Each new seam gets a SQL Server implementation that delegates to today's classes unchanged: `TSqlText`, `CanonicalScriptWriter`, `SqlServerSchemaReader`, `ScriptGenerator`, `Ddl`, `ScriptText`, `ApplyService.RunFileAsync/InspectAsync`. PostgreSQL and MySQL get **new, small** generators built on the engine-neutral `SchemaSnapshot`. They reuse the pure planner helpers that are extracted from `CoreRun` (§2.6).

### 2.2 Projects

| Project | References | Contents |
|---|---|---|
| `SchemaScope.Core` (changed, **no driver packages**) | as today | New `Engines/` namespace: `DatabaseEngine`, `IEngineProvider`, `IConnectionService`, `ISqlDialect`, `IServerIdentity`, `EngineCapabilities`, `EndpointInfo`, `EngineRegistry`. Plus `CrossEngine/` (`LogicalType`, `ITypeMap`, `CrossEngineMatcher`, `CrossEngineClassifier`). |
| `SchemaScope.SqlServer` (changed) | as today | `SqlServerEngineProvider`; `SqlServerConnectionService : IConnectionService`, which adds `ParseEndpoint`, `Mask` and `SecretValues` by moving the `SqlConnectionStringBuilder` code out of `SchemaScopeApi.SavedServer/LoginOf/Endpoint`. `FromSysType` moves here, with an `[Obsolete]` forwarder left in Core. |
| `SchemaScope.PostgreSql` (new, R1) | Core, Npgsql | `PgEngineProvider`, `PgConnectionService`, `PgSchemaReader`, `PgDialect`, `PgTypeFormatter`, `PgIdentity` |
| `SchemaScope.Scripting` (changed) | as today | `IScriptGenerator`; `SqlServerScriptGenerator` (an adapter over the untouched `ScriptGenerator`); `Planning/` (pure helpers extracted from `CoreRun`, with forwarders left in place) |
| `SchemaScope.Scripting.PostgreSql` (new, R2) | Scripting, SchemaScope.PostgreSql | `PgScriptGenerator`, `PgDdl`, `PgStatementSplitter`, `PgScriptValidator`, PG probes |
| `SchemaScope.MySql` + `SchemaScope.Scripting.MySql` (new, R5) | MySqlConnector | Same shape as the PostgreSQL projects. MySQL and MariaDB share one provider and differ by flavour and version flags. |
| `SchemaScope.Api` (changed) | + provider projects | `EngineRegistry` in DI; `IApplyExecutor` with `SqlServerApplyExecutor` (moved method bodies) and `PgApplyExecutor` / `MySqlApplyExecutor`; `IScriptGeneratorFactory`; `GET /api/engines` |
| `tests/SchemaScope.Engines.Tests` (new) | Api + providers, Testcontainers.PostgreSql/MySql/MariaDb | Every Docker-backed test (§11) |

`QueryScope.*` keeps referencing only Core and SqlServer, so it gains no drivers and its exe size is unchanged. `ViewerHost` never registers providers or executors.

### 2.3 Core contracts (R0)

```csharp
namespace SchemaScope.Core.Engines;

public enum DatabaseEngine { SqlServer = 0, PostgreSql = 1, MySql = 2, MariaDb = 3, Oracle = 4, Sqlite = 5 } // append-only, persisted as int
public enum EngineFeature { Compare, Scripts, ObjectScripts, Apply, Destructive, CrossCompare, CrossScripts }
public enum TransactionSemantics { PerFileTransactional, NoDdlRollback }
public enum IdentifierFold { Upper /* SQL Server today */, Exact /* PG: catalog already folded */, Lower /* MySQL lctn=1/2, columns */ }

public sealed record EngineCapabilities(
    bool Schemas, bool Sequences, bool Synonyms, bool TableTypes, bool AliasTypes,
    bool FilteredIndexes, bool IncludeColumns, bool RoutineOverloads, bool ByteExactModuleText,
    TransactionSemantics Ddl, int MaxIdentifierBytes, IReadOnlySet<DbObjectType> ObjectTypes,
    IReadOnlySet<EngineFeature> Features, bool Preview);

public sealed record EndpointInfo(DatabaseEngine Engine, IReadOnlyList<string> Hosts, int? Port,
    string? Instance, string Database, string LoginDescription);

public interface IConnectionService
{
    DatabaseEngine Engine { get; }
    string BuildConnectionString(ConnectionSettings s, string? overrideDatabase = null);
    Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseListItem>> ListDatabasesAsync(string connectionString, CancellationToken ct = default);
    string? BrowseDatabase { get; }                          // "master" | "postgres" | null (MySQL)
    EndpointInfo? ParseEndpoint(string connectionString);    // null => caller treats as Protected and releases no secret
    IReadOnlyList<string> SecretValues(string connectionString); // Password, SSL Password, CertificatePassword, ...
    string Mask(string connectionString);
}

public interface ISqlDialect
{
    DatabaseEngine Engine { get; }
    IdentifierFold Fold { get; }
    string DefaultSchema { get; }                            // dbo | public | <database>
    IReadOnlyList<string> DefaultExcludedSchemas { get; }
    string Quote(string identifier);
    string LightNormalize(string body);                      // SqlServer => TSqlText.Light
    string CanonicalHeader(string body, DbObject o);         // SqlServer => TSqlText.CanonicalHeaderName; PG/MySQL => no-op
    string StripComments(string body);                       // PG: $tag$/E'' aware, nested /* */; MySQL: #, backslash, non-nesting
    string Loosen(string text);                              // strips this dialect's quote characters
    bool CanDeepParse { get; }                               // ScriptDom: SqlServer only
    bool ParsesIdentically(string a, string b);              // false when no parser: never claims FormattingOnly falsely
    string CanonicalScript(DbObject o, CompareOptions opt);  // SqlServer => CanonicalScriptWriter.Write
    string MonacoLanguage { get; }                           // "sql" | "pgsql" | "mysql"
    string WordSeparators { get; }                           // also feeds ReferenceFinder.IsWordChar
}

public interface IServerIdentity
{
    IReadOnlyList<string> HostKeys(EndpointInfo e);          // host-only keys for the Protected rule (cross-engine)
    string TargetKey(EndpointInfo e, string database);       // engine|host|port|db for the apply lock
}

public interface IEngineProvider
{
    DatabaseEngine Engine { get; }
    string DisplayName { get; }
    EngineCapabilities Capabilities { get; }
    ISqlDialect Dialect { get; }
    IConnectionService Connections { get; }
    IServerIdentity Identity { get; }
    ISchemaReader CreateReader();
}

public sealed class EngineRegistry(IEnumerable<IEngineProvider> providers)
{
    public IEngineProvider Get(DatabaseEngine e);            // throws EngineNotAvailableException
    public bool Supports(DatabaseEngine e, EngineFeature f);
    public IReadOnlyList<IEngineProvider> All { get; }
    public static EngineRegistry SqlServerOnly();            // legacy ctors and tests
}
```

Scripting and Apply contracts:

```csharp
public interface IScriptGenerator
{
    Task<ScriptOutcome> GenerateAsync(ScriptRequestContext req, CancellationToken ct, IProgress<ScriptProgress>? p);
}
public sealed record ScriptRequestContext(ScriptEndpoint Source, ScriptEndpoint Target, SchemaSnapshot SourceSnap,
    SchemaSnapshot TargetSnap, CompareResult Result, int TargetIndex, ScriptOptions Options,
    IReadOnlySet<string> FormattingCandidates, IReadOnlyDictionary<string, LineChange> DifferentCandidates);
public interface IScriptGeneratorFactory { IScriptGenerator? For(DatabaseEngine source, DatabaseEngine target); } // null => 400

public interface IStatementSplitter { IReadOnlyList<SqlBatch> Split(string text); } // SqlServer => ScriptText.SplitBatches (GO)

public interface IApplyExecutor
{
    DatabaseEngine Engine { get; }
    TransactionSemantics Transactions { get; }
    IStatementSplitter Splitter { get; }                     // the SAME instance the validator uses (parity test)
    Task<TargetFacts?> InspectAsync(string cs, CancellationToken ct = default);
    string? Preflight(IReadOnlyList<VerifiedFile> files, TargetFacts facts); // SqlServer => MemoryOptimizedProblem
    string TargetKey(TargetFacts? facts, string server, string database);      // SqlServer => today's format
    Task RunFileAsync(string cs, string database, VerifiedFile file, CancellationToken abort,
        Func<bool> beforeCommit, Action<int> statementDone);
    string DescribeError(Exception ex, SqlBatch statement);  // "Msg N, line L" | "ERROR <SqlState> at statement N (line L)" | "Error 1050 ..."
}

public sealed record ValidationLevel(bool Parsed, bool ModuleTextProven, string? NotValidatedReason); // on ValidationSummary
public enum ProbeResultKind { Ok, NotChecked, NotApplicable } // NotApplicable: no counterpart on this engine; never a hold
```

`ApplyService` keeps its static `InspectAsync`, `TargetKey`, `MemoryOptimizedProblem`, `SqlcmdSession`, `SqlcmdOptions` and `Verify(files, out verified)` as forwarders to the SQL Server executor, so `ApplySafetyTests` and `ApplyIntegrationTests` compile unchanged. `RunAsync` catches `DbException` instead of only `SqlException`, so `FinishApplyRun` always runs ("no record, no apply") whichever driver throws.

### 2.4 Model changes (all additive; old JSON stays readable)
- **`ConnectionSettings`**:
  - New fields: `Engine = SqlServer`, `int? Port`, `SslMode? Ssl`, and `Dictionary<string,string>? EngineOptions`. EngineOptions is validated per engine and must never hold a secret.
  - `AuthMode` is append-only. New values: `Password = 5` (generic user/password for non-SQL engines), `Kerberos = 6`, `ClientCertificate = 7`. Each engine allows a subset, declared in its capabilities. `UsesPassword` reads that subset.
- **Engine fields**, all defaulting to SqlServer: `SchemaSource.Engine`, `SchemaSnapshot.Engine` + `EngineVersion` + `EngineFacts` (e.g. `lower_case_table_names`, `server_version_num`), `TargetSummary.Engine`, `SnapshotInfo.Engine`, `CompareHistoryDatabase.Engine` (part of `BuildSignature` only when it is not SqlServer), `ApplyTarget.Engine`, `ApplyRunRecord.Engine`.
- **`ScriptEndpoint(Label, Server, Database, ConnectionString, DatabaseEngine Engine = DatabaseEngine.SqlServer)`**: a trailing optional positional parameter, so the roughly 10 existing test constructions still compile. It is set server-side only, in `SchemaScopeApi.Endpoint(SchemaSource)`.
- **`DbObject`** gains `IdentifierFold Fold = Upper` and `string? Signature` (PG overloads). The `Key` depends on the fold:
  - `Upper`: exactly today's string, `$"{(int)Type}|{SCHEMA}.{NAME}"`, unchanged byte for byte.
  - `Exact`: `$"{(int)Type}|{Schema}.{Name}"`, with `({Signature})` appended when set.
  - `Lower`: lower-invariant.
- **`SchemaSnapshot.BuildIndex()`** stops silently overwriting duplicates. It keeps **both** objects: the second gets a `#2` key suffix, and a snapshot warning is recorded. SQL Server same-engine runs never produce duplicates, so their behaviour does not change.
- **`DbObjectType`** gets new members appended only: `MaterializedView = 12, Domain, EnumType, CompositeType, Extension, Event, Package, PackageBody`. `Label` and `IsModule` are updated.
- **`ObjectStatus`** (R4) gets `Equivalent = 6` (severity of FormattingOnly; `IsDifference = false`) and `NotComparable = 7` (severity of Unavailable; `IsDifference = true`, so it is counted and never hidden). `TargetCell` gains `IReadOnlyList<string>? MappingNotes`.
- **`ColumnInfo`** (R4) gains `LogicalType? Logical`, filled by every reader (§4.1). Same-engine compare never reads it, so same-engine hashes do not change.
- **`StructureSection`** gains `kind` (default `"columns"`), so the UI stops matching on the English title.

### 2.5 Data flow (after R1)
1. `/api/compare` carries connection ids only. Inline settings now also go through `TryRehydrate`, which closes an existing gap.
2. `CompareService.Resolve` loads `ConnectionSettings`, calls `registry.Get(s.Engine).Connections.BuildConnectionString`, and builds `SchemaSource{Engine}`.
3. `new CompareRunner(src => registry.Get(src.Engine).CreateReader())`. The old constructor `CompareRunner(ISchemaReader)` is kept, so `RunnerOrderTests` and the Spike are unaffected.
4. Each reader normalizes with `new SchemaNormalizer(options, provider.Dialect)`. The old `SchemaNormalizer(options)` constructor still exists and defaults to the SQL Server dialect. Normalization **stays inside the reader**, the way the SQL Server reader does it today, so there is no move and no risk of normalizing twice.
5. `CompareEngine` handles each target on its own:
   - **Same engine as the source:** today's Classify ladder. The deep parse runs only if `dialect.CanDeepParse`.
   - **Different engine (R4):** `CrossEngineMatcher` followed by `CrossEngineClassifier` (§4). Hashes, LooseHash and ScriptDom are never used.
6. **Scripts:** `IScriptGeneratorFactory.For(src, tgt)` returns:
   - SqlServer to SqlServer: the untouched `ScriptGenerator`.
   - PG to PG: `PgScriptGenerator`.
   - Cross-engine (R4b): `CrossEngineScriptGenerator`.
   - anything else: null, and the route answers 400 "not available for <pair> in this version".
7. **Apply:** gated by `registry.Supports(engine, Apply)`, then the `IApplyExecutor` for that engine runs. The engine always comes from `session.TargetConnections[i]`, never from the request body.

### 2.6 Where existing components go

| Component | Destination |
|---|---|
| `TSqlText`, `CanonicalScriptWriter`, `SchemaNormalizer` statics | Stay in Core. `SqlServerDialect` delegates to them. `TsqlTokens.Light` and `ApplyService` keep calling them directly. |
| `SqlServerSchemaReader`, `CatalogQueries`, `SqlTypeFormatter` | Stay. The reader sets `Engine = SqlServer` and, from R4, fills `Logical`. |
| `SqlServerConnectionService` | Implements `IConnectionService`. It is still registered as a concrete singleton for QueryScope and tests. |
| `ServerNames` | Stays as the SQL Server identity. Its loopback, machine-name and DNS-suffix folding is extracted into `HostNames`, which the other engines use. |
| `ScriptGenerator` + `Scripting/Generators/*`, `Ddl`, `ScriptText`, `TsqlTokens`, `ModuleHeader`, `ScriptValidator`, `TargetProbes` | **Untouched.** Wrapped by `SqlServerScriptGenerator`. |
| Pure parts of `CoreRun`: `StableOrder`, the "source rows the target lacks" helpers `Todo`/`Pairs`, fixpoint hold propagation | Moved into `Scripting/Planning/` with forwarders left behind, so the PG and MySQL generators reuse them instead of copying them. |
| `ScriptWriter`, `ReviewLog`, `FolderResult`, `HeldBackItem`, `ScriptOutcome`, `ApplyPlans`, `ApplyPlanStore`, `WriteMode` | Reused by every engine as is. `ScriptWriter` gets a per-dialect BOM flag: no BOM for psql or mysql. |
| `ObjectScripts` (single-object scripts) | SQL Server only until R2b. Other engines get 400. |
| `ExportService`, `ShareStore`, `ViewerHost`, `ReferenceFinder`, `LineDiffService` | Unchanged. The engine label reaches them through the DTOs. |
| `DefinitionCache` | `PathFor` is unchanged for SQL Server. Other engines would hash `engine|server|port|database`. The PG and MySQL readers fetch module bodies in bulk and do not use the cache until timings show they need it. |

---

## 3. Engine support matrix

Legend: ✅ supported · 👁 Preview, behind the "Show preview engines" setting · 📁 written to a folder only, no in-app apply · ✋ code objects are held back in 99_review · ❌ not supported in that release.

| Release | Engine / pair | Read | Compare | Create scripts | Validate | Apply (Write mode) |
|---|---|---|---|---|---|---|
| today / R0 | SQL Server ↔ SQL Server | ✅ | ✅ | ✅ | ✅ ScriptDom | ✅ per-file transaction |
| R1 | PostgreSQL ↔ PostgreSQL | 👁 | 👁 | ❌ | ❌ | ❌ |
| R2 | PostgreSQL ↔ PostgreSQL | ✅ | ✅ | 📁 | ✅ libpg_query if adopted, otherwise lexical (labelled) | ❌ |
| R3 | PostgreSQL ↔ PostgreSQL | ✅ | ✅ | ✅ | as R2 | ✅ per-file transaction |
| R4 | SQL Server ↔ PostgreSQL | ✅ | ✅ structural; Equivalent / NotComparable | ❌ | n/a | ❌ |
| R4b | SQL Server → PostgreSQL | ✅ | ✅ | 📁 structure only, ✋ | as R2 | ❌ always |
| R5 | MySQL 8.0/8.4, MariaDB 10.11/11.4 | ✅ | ✅ | 📁 | lexical only (labelled "not validated") | ❌ |
| R5 | MySQL ↔ SQL Server / PostgreSQL | ✅ | ✅ structural | 📁 structure only, ✋ | lexical | ❌ always |
| R6 | MySQL / MariaDB same-engine | ✅ | ✅ | ✅ | lexical | ✅ one statement per file, no rollback, extra acknowledgement |
| later | SQLite, Oracle | on demand only (§14) | | | | |

Destructive (`11_destructive`): SQL Server today; PostgreSQL in R3b (opt-in); not in R6 for MySQL; never across engines.

---

## 4. Cross-engine compare rules

### 4.1 Canonical (logical) model
Each reader fills a dialect-neutral layer **next to** the native one. Same-engine compare keeps using native fields and hashes. Cross-engine compare uses only the logical layer.

```csharp
public enum LogicalFamily { Bool, Int, Decimal, Float, Money, String, Binary, Date, Time, Timestamp, TimestampTz,
    Interval, Uuid, Json, Xml, Enum, Array, Spatial, RowVersion, Other }
public sealed record LogicalType(LogicalFamily Family, int? Bits = null, bool Signed = true, int? Precision = null,
    int? Scale = null, int? Length = null /* characters; null = unbounded */, bool Fixed = false, bool? Unicode = null,
    int? FractionalSeconds = null, IReadOnlyList<string>? EnumValues = null, string Native = "");
public enum MapExactness { Exact, Equivalent, Lossy, None }
public interface ITypeMap { (string NativeTarget, MapExactness Level, string? Why) Map(LogicalType t, DatabaseEngine target); }
```

The canonical comparison also needs these neutral facets:
- identity kind (Identity / GeneratedAlways / ByDefault / AutoIncrement / Serial) with start and increment;
- default kind (None / Literal / Function / Expression) with a normalised value;
- generated column (Stored / Virtual);
- FK action enum including RESTRICT;
- index method (BTree / Hash / Gin / Gist / FullText / Spatial / Other).

### 4.2 Matching
- `CompareOptions.SchemaMap` (source schema to target schema) is editable in Options and **saved in profiles**. Defaults are `dbo ↔ public`. For MySQL, every source schema maps to the connected database, with a warning if more than one source schema exists.
- The match key is `(int)Type | lower(map(schema)).lower(name)`, using `ToLowerInvariant` only. PG routines additionally match on the logical parameter signature.
- **Ambiguity:** if two target objects fold to the same key (PG `"Order"` and `order`), the source row is **Different/Ambiguous** with the note "2 target objects differ only by case". It is never Missing. The script planner never emits CREATE for an ambiguous row, and a test asserts that.
- System-named PK/UQ/FK are matched **by column set, never by name**: SQL Server `PK__x__hex`/`UQ__`/`DF__`, PostgreSQL `*_pkey`/`*_fkey`/`*_key`, MySQL `PRIMARY`/`*_ibfk_N`.
- Physical options are never compared across engines: fill factor, padding, filegroup or tablespace, clustered vs heap, compression, storage engine. Collation is reduced to a case-insensitive vs case-sensitive behaviour flag. A mismatch there produces a warning, for example "UNIQUE on this column accepts 'A' and 'a' on the target".

### 4.3 Type-equivalence table (excerpt; full matrix lives in code with one test row each)

| SQL Server | PostgreSQL | Level | MySQL 8 | Level |
|---|---|---|---|---|
| `bit` | `boolean` | Equivalent | `tinyint(1)` | Equivalent |
| `tinyint` | `smallint` | Equivalent (widening) | `tinyint unsigned` | Exact |
| `smallint`/`int`/`bigint` | same | Exact | same | Exact |
| `decimal(p,s)` | `numeric(p,s)` | Exact | `decimal(p,s)` | Exact |
| `money` | `numeric(19,4)` | Equivalent | `decimal(19,4)` | Equivalent |
| `float(53)`/`real` | `double precision`/`real` | Exact | `double`/`float` | Exact |
| `nvarchar(n)`/`varchar(n)` | `varchar(n)` | Equivalent (bytes vs characters) | `varchar(n)` utf8mb4 | Equivalent |
| `nvarchar(max)`/`text` | `text` | Equivalent | `longtext` | Equivalent |
| `varbinary(n/max)` | `bytea` | Equivalent | `varbinary(n)`/`longblob` | Equivalent |
| `date` | `date` | Exact | `date` | **Lossy** (range starts at 1000) |
| `datetime` | `timestamp(3)` | Equivalent | `datetime(3)` | Equivalent |
| `datetime2(p≤6)` | `timestamp(p)` | Exact | `datetime(p)` | Lossy (range) |
| `datetime2(7)` | `timestamp(6)` | **Lossy** | `datetime(6)` | **Lossy** |
| `datetimeoffset` | `timestamptz` | **Lossy** (offset not kept) | none | **None** |
| `uniqueidentifier` | `uuid` | Exact | `binary(16)`/`char(36)` | Equivalent |
| `xml` | `xml` | Equivalent | `longtext` | Lossy |
| `json` (SQL 2025) | `jsonb` | Equivalent | `json` | Equivalent |
| `IDENTITY(s,i)` | `GENERATED BY DEFAULT AS IDENTITY` | Equivalent | `AUTO_INCREMENT` | Equivalent if i=1, else **Lossy** |
| `rowversion`, `sql_variant`, `hierarchyid`, table types | none | **None** | none | **None** |

The reverse directions (PG/MySQL to SQL Server, PG ↔ MySQL) are covered by the same matrix. Examples: PG unconstrained `numeric` to `decimal(38,s)` is Lossy; PG `interval` and arrays are None; MySQL `SET` is None. Compare, StructureDiff, the `column_type_differences` report and the emitter all read this one table, so they can never disagree.

### 4.4 Statuses for a cross-engine pair
**Tables, columns, keys, indexes, sequences** are compared structurally:

| Status | Condition |
|---|---|
| **Same** | Every mapping is Exact and every facet is equal. |
| **Equivalent** (≈, new) | At least one mapping is Equivalent, and nothing else differs. Each mapping used is listed as a note, e.g. `nvarchar(50) → varchar(50): bytes vs characters`. |
| **Different** | Any real difference. This includes **any Lossy mapping** (with its reason) and **any type with no equivalent**. Lossy is never shown as Same or Equivalent. |

**Expressions** (checks, computed columns, filtered-index predicates, non-literal defaults):
- Literals are normalised: `((0))`→`0`, `N'x'`→`'x'`, bit `1`↔`true`, `'0'::integer`→`0`.
- About 15 functions are mapped by a hand-curated table: `getdate()/sysdatetime()` ↔ `LOCALTIMESTAMP`/`CURRENT_TIMESTAMP(6)`, `getutcdate()` ↔ `now() at time zone 'utc'`/`UTC_TIMESTAMP(6)`, `newid()` ↔ `gen_random_uuid()`/`UUID()`, `NEXT VALUE FOR` ↔ `nextval`.
- A simple-AST normaliser handles comparisons, `IN`, `AND`/`OR`, `IS NULL` and literals.
- Anything outside that is "expression not comparable across engines". The **table becomes Different** with the note "1 expression could not be compared", so the structural result stays visible.

**Views, procedures, functions, triggers** are compared on existence and signature: parameter names, modes and logical types, return type; output columns for views; timing, events and level for triggers.
- Signature matches: **NotComparable** (⇄, new): "different languages (T-SQL vs PL/pgSQL), body not compared".
- Signature differs: **Different**.

**Kinds with no counterpart** (Synonym, TableType and AggregateFunction going to PG/MySQL; Extension, MaterializedView and EnumType going to SQL Server) show as **Missing or Extra** with the note "no equivalent on <engine>".

`Unavailable` keeps exactly its current meaning: encrypted, or the login has no permission. A MySQL `NULL ROUTINE_DEFINITION` also maps to Unavailable. It is **never reused** for cross-engine cases.

### 4.5 Honesty contract (each rule is a named test)
- **H1** Across engines, nothing is Same unless every compared facet is Exact.
- **H2** Across engines, nothing is ever FormattingOnly. Hash, LooseHash and ScriptDom never run on a cross-engine pair.
- **H3** NotComparable only when the signature or shape matches.
- **H4** Every Equivalent cell carries a mapping note. A property test over the whole type map asserts that no Lossy or None mapping yields Same or Equivalent.
- **H5** Run-level warning summary, for example: "SQL Server 16.0 → PostgreSQL 16.4: 12 objects not comparable (different languages), 4 columns use lossy mappings, 3 expressions not compared, 2 ambiguous case matches."
- **H6** Match % counts only Same + Equivalent. Equivalent and NotComparable are counted separately.
- **H7** Identifiers generated for PG are always quoted and checked against the 63-**byte** limit. A name that is too long is held back, never truncated.

### 4.6 Cross-engine scripts (R4b, folder only)
These are built from the logical model through the target dialect's DDL builder. Source text is never copied or translated.
- **Generated:** CREATE SCHEMA; CREATE TABLE and ADD COLUMN for Exact/Equivalent columns; NULL/NOT NULL; identity (`GENERATED BY DEFAULT AS IDENTITY`; MySQL only when the increment is 1); PK/UQ/FK; plain indexes (+INCLUDE on PG 11+); sequences; literal defaults and defaults from the function table, each with a version probe (PG ≥ 13 for `gen_random_uuid`, MySQL ≥ 8.0.13 for expression defaults).
- **Held in 99_review with a reason:** Lossy/None types; computed columns; checks outside the AST subset; filtered indexes; every view, routine and trigger (a signature stub with `-- TODO: port body` at most); collation and CI semantics; identifiers over the length limit or colliding (PG index names are unique per schema); ambiguous rows; MySQL AUTO_INCREMENT with an increment other than 1.
- Folders 02, 04, 07, 08, 09 and 10 report "not applicable for SQL Server → PostgreSQL". 11 is refused. `column_type_differences.md` shows source type, mapped type, actual target type and exactness for each column.
- Identifier policy is chosen per run ("quote and preserve case", the default, or "fold to lower") and recorded in the README.
- **Write mode is refused for every cross-engine plan** on the server side.

---

## 5. Write mode per engine

### 5.1 Common gates (unchanged order, every engine)
1. Write mode is on (30-minute idle timeout).
2. Plan id is bound to the run and the target.
3. Typed database name matches.
4. Textual Protected check refuses protected targets.
5. IsSource refuses the source database.
6. Requested sections are a subset of the plan.
7. The destructive acknowledgement is present when needed.
8. Server-reported Protected re-check.
9. Per-target lock.
10. The plan is single-use.
11. SHA-256 re-verify of every file.
12. The audit record is written before anything runs.

New gates, checked first:
- `registry.Supports(engine, Apply)` on the server. Engines or phases without Apply get 400.
- Cross-engine plans never get Apply.
- For non-SQL Server engines, `InspectAsync == null` **refuses**. SQL Server keeps today's behaviour.
- The lock key and IsSource include the engine and port.
- Typed name comparison is `Ordinal` (exact case) on PostgreSQL, and on MySQL when `lower_case_table_names = 0`. SQL Server keeps OrdinalIgnoreCase. The UI applies the same rule, read from the engine descriptor.

### 5.2 SQL Server (unchanged)
`SqlServerApplyExecutor` holds the moved method bodies: sqlcmd session SETs (@@OPTIONS 5176), GO splitting, a SqlTransaction per file, the DB_NAME() check, and the MemoryOptimized preflight. The existing LocalDB tests show the behaviour is unchanged.

### 5.3 PostgreSQL (R3): transactional, the same promise as SQL Server
For each file:
1. Open an Npgsql connection, check `SELECT current_database()` with an exact match and that the server is the one the gates checked (the same identity key: a host list can fail over between files), then run `SET application_name='SchemaScope'`.
2. Run `BEGIN; SET LOCAL lock_timeout = '5s'; SET LOCAL idle_in_transaction_session_timeout = …; SET LOCAL search_path = …`.
3. Run the statements one at a time. `PgStatementSplitter` splits on `;` outside quotes, `$tag$` bodies and comments, and keeps start lines. The **same splitter** is used by `Verify` and by the validator, enforced by a parity test.
4. `beforeCommit()` checks that write mode is still on, then COMMIT. Any error or stop leads to ROLLBACK. Cancel uses `NpgsqlCommand.Cancel` followed by a rollback.

Other rules:
- **Preflight refuses** any file containing `CREATE/DROP/REINDEX … CONCURRENTLY`, `VACUUM`, `ALTER SYSTEM`, `CREATE/DROP DATABASE|TABLESPACE`, or an `ALTER TYPE … ADD VALUE` whose new value is used in the same file. The generator never emits these; the preflight is a server-side safety net.
- Errors are reported as `ERROR <SqlState> at statement N (line L): <message>`, scrubbed. `PostgresException.Detail`, `Where` and `Hint` are dropped because they can echo row values.
- **Identity:** `InspectAsync` returns `system_identifier` (`pg_control_system()`) + `inet_server_port()` + `current_database()`. If the deploy login cannot read `system_identifier`, fall back to `inet_server_addr()` + port. If there is still no identity, refuse. Test this with a non-superuser deploy role in the container.
- R3b (optional): PG `11_destructive` with probe-before-drop and `DROP … RESTRICT` as the backstop.

### 5.4 MySQL / MariaDB (R6): no DDL rollback, so a different design
Every DDL statement commits implicitly. MySQL 8 and MariaDB 10.6+ only guarantee that a single statement is atomic. The design does not pretend otherwise:
1. **One DDL statement per file.** The MySQL generator writes exactly one statement per `objects\NNN_*.sql`. `Verify` refuses a MySQL plan file that splits into more than one statement, which makes the file the atomic unit. Statements are idempotent where the dialect allows it (`IF [NOT] EXISTS`, MariaDB `CREATE OR REPLACE`).
2. **Honest wording everywhere** (ApplyDialog, PlanSection warnings, README, audit): "MySQL cannot roll back schema changes. Each file is one statement; if one fails, files before it stay applied." The words "rolled back" never appear for this engine, and a test asserts that.
3. **Extra server-side acknowledgement:** `ApplyRequest.AcceptNoRollback = true` is required whenever `Transactions == NoDdlRollback`. Without it the request gets 400, the same pattern as `AcceptDataLoss`.
4. **Stop on first failure, then resume by re-compare and regenerate.** The plan is consumed. Because scripts are derived from the compare, the new plan contains only what is still missing. There is no statement journal to replay. `apply_batches` records each file with its statement index for the audit.
5. Stop and write-mode-off are checked **before each statement**. A running statement is allowed to finish, and the UI says so. "Cancel now" sends `KILL QUERY` on a second connection and reports that statement's outcome as *unknown*; the next compare shows what actually happened.
6. **Session settings:** `lock_wait_timeout=5`, strict `sql_mode`, `SET NAMES utf8mb4`. **Connection settings:** `AllowLoadLocalInfile=false`, `AllowUserVariables=false`, and `AllowPublicKeyRetrieval` only when TLS is on.
7. **Read-only probes before apply become hold reasons**, not warnings: duplicates under new UNIQUE/PK, FK orphans, NOT NULL feasibility, column fit.
8. **Not offered on MySQL:** mixing DDL and DML in one file, `08`/`10` module swaps (DROP+CREATE cannot be atomic; changed routines go to 99_review, or use `CREATE OR REPLACE` on MariaDB only), and `11_destructive`.

### 5.5 Manual runners
The per-folder `run_all.ps1` is chosen per engine:
- SQL Server: `sqlcmd -E` (as today).
- PostgreSQL: `psql -v ON_ERROR_STOP=1 --single-transaction -f`, with the password from a PGPASSFILE outside the run folder or a prompt.
- MySQL: `mysql --defaults-extra-file=<prompted temp file>`.

Passwords are never written into scripts, and `PsQuote` wraps every interpolated value. `.sql` files for PG and MySQL are written without a BOM. SHA-256 is still computed from the in-memory bytes.

---

## 6. Security

1. **Secret release.** Fixed in R0a, before any engine selector exists.
   - `SameDestination` = Engine == && EffectivePort == (null resolves to the engine's default port) && Server (case-insensitive) && UserName && Auth && UseRawConnectionString. For raw strings it also requires the same host set and port, as parsed by the provider's `ParseEndpoint`. If either side cannot be parsed, nothing is released.
   - There is **one implementation**. The private copy in `ConnectionSecrets` is deleted, and `SchemaScopeApi.TryRehydrate` becomes an internal forwarder, so the 8 existing tests stay unchanged. `SchemaScopeStore.SaveConnection` uses the same rule, so changing the engine or port drops the stored password.
   - A raw string whose engine does not match the selected engine (e.g. an Npgsql string on a SQL Server connection) is refused.
   - **New SecurityTests:** engine swap, port change, host-list change, a raw Npgsql string with a different host, an unparseable raw string, QueryScope parity, and "a password does not follow a row from `connections` to `engine_connections`".
2. **Masking and scrubbing.**
   - `Mask` and `SecretValues` use each driver's own builder and cover `Password`, `SSL Password`, `Passfile`, `CertificatePassword` and similar keys.
   - `SecretScrubber` takes the union of all providers' keys plus a broadened rule: any key containing *password*, *pwd*, *passphrase* or *secret*.
   - **URI-form strings** (`postgres://user:pass@host`) are refused at input.
   - New secrets go only through DPAPI `SecretProtector`, never into `EngineOptions`.
   - Probe reasons drop `PostgresException.Detail/Where/Hint` and are truncated, as `SqlProbe.Reason` does.
3. **Auth per engine.**
   - SQL Server: today's modes.
   - PostgreSQL: Password (SCRAM/md5), Kerberos/SSPI via Integrated Security, client certificate.
   - MySQL: Password (caching_sha2_password, native), client certificate.
   - The API validates that the chosen AuthMode is allowed for the engine.
   - TLS defaults: PG `SslMode=VerifyFull`, MySQL `SslMode=VerifyFull`. "Trust server certificate" is an explicit opt-in that maps to `Require` plus trust.
4. **Protected matching** (fail closed).
   - A Protected saved connection protects **every engine and every port on that host**. Hosts are normalised by `HostNames`: loopback and machine name folded, DNS suffix dropped, port split off by the provider.
   - Multi-host strings are protected if any host is.
   - A string that cannot be parsed is treated as Protected. Today's fail-open fallback to the Server box is removed.
   - The server-reported re-check uses `IApplyExecutor.InspectAsync`. On non-SQL engines, null means refuse.
   - On non-SQL engines the reported address alone is no proof (an IP for a name, a LAN address for `localhost`, a DNS alias, NAT): the target's hosts and reported address are resolved, as is every Protected connection's host (this machine's addresses count as one), and one address in common refuses. Each Protected connection to the same engine is then asked who it is (`InspectAsync` on its own saved string, read-only) and the same cluster (`system_identifier`, else address and port) refuses. A name that cannot be resolved, or a Protected connection that cannot be asked, refuses (fail closed).
   - New WriteModeTests rows: `localhost:5432` vs `localhost`, `Host=prod,localhost`, an unparseable string, and PG blocked by a protected SQL Server connection on the same host.
5. **Engine provenance.** `SchemaSource.Engine` and `ScriptEndpoint.Engine` are set server-side from stored settings only. New DTO fields follow the existing `[JsonIgnore]` pattern.
6. **Loopback token, Host-header check, Debug-only CORS, CSP:** unchanged. `GET /api/engines` returns capabilities only, no secrets. It requires the token, is added to `OwnerRouteTests.NewOwnerRoutes`, and is not on the viewer.
7. **Share viewer.** `ViewerHost.Build` never calls provider registration. The existing exact GET-route test stays green. A **new test** asserts that the viewer's container resolves no `IEngineProvider`, `EngineRegistry` or `IApplyExecutor`.
8. **Read-only by default.** PG and MySQL readers and probes run in `BEGIN READ ONLY` / `START TRANSACTION READ ONLY`, with `statement_timeout` / `max_execution_time`. Data probes stay gated by `CheckExistingData`. There is no in-app "dry run" (BEGIN…ROLLBACK) on real servers, because it still takes ACCESS EXCLUSIVE locks.
9. **Audit.** The `DbException` catch keeps the audit complete. `apply_runs.engine` and a provider-described login (e.g. "PostgreSQL user: deploy") are recorded.
10. **Old builds.** Non-SQL connections live in a separate table that shipped builds never read (§8).

---

## 7. UI changes

- **Engine catalogue:** `GET /api/engines` is cached in `web/src/engines.ts`. All engine-specific strings are rendered from it: auth labels, placeholders, hints, Advanced toggles, sections, transaction wording. This replaces `AUTH_LABELS`, `AUTH_SHORT` and `BLANK_CONNECTION` and fixes the ConnectionPicker `split(' ')[0]` "Microsoft" bug.
- **ConnectionForm:**
  - The Engine select is the first field and is shown only when more than one engine is available. Preview engines appear only with Settings › "Show preview engines". The server enforces capabilities regardless of the toggle.
  - Separate Port field. SSL mode for PG and MySQL. Encrypt, TrustServerCertificate and Read-only intent are SQL Server only.
  - Browse uses `BrowseDatabase`.
  - Changing the engine clears the password and shows "password will need re-entering".
- **Badges:** engine chips (e.g. "PG 16", "SQL 2022") on ConnectionsView, ConnectionPicker (also in its search text), SetupBar slots and chips, Dashboard tiles (with version), history, exports and shares. SetupBar shows a "cross-engine" chip when the source and a target differ. Before R4 that pair is blocked with a clear message, and the server refuses it too. Swap warns when the script dialect would change.
- **Identity colours:** `dbKey` becomes `engine|server|port|database` for non-SQL engines; SQL Server keys are unchanged. `ApplyDialog.matchFor` uses the same identity.
- **DiffPanel:** uses `detail.language` and engine word separators. For cross-engine pairs it shows plaintext with side labels ("T-SQL" | "PL/pgSQL") and a banner, and no change counts for NotComparable.
- **StructureGrid:** sections keyed by `kind`. From R4, shows native type, logical family and the mapping note, with both sides' defaults.
- **status.ts (R4):** `equivalent` (≈) and `notComparable` (⇄). STATUS_ORDER, TYPE_COUNT_ORDER, Dashboard segments and typeCounts are updated. `Record<ObjectStatus,…>` makes the compiler enforce every map. The cross-engine banner and legend are always shown on such runs. The Unavailable description is per engine (MySQL: "needs SHOW_ROUTINE or definer").
- **OptionsDialog:** SQL Server-only toggles (FillFactor, IndexPadding, FileGroups, NotForReplication, SmartParse) are hidden when no side is SQL Server. Excluded-schema defaults come from the dialect. From R4: schema-map editor and identifier policy. New `TYPE_LABELS` for the appended types.
- **ScriptDialog:** sections, hints and runner name come from the server for each pair. The validation banner states its level ("Parsed with the PostgreSQL parser" / "Lexical check only – not validated"), and Passed is never claimed without a parser. Cross-engine runs show a prominent "N items held for review".
- **ApplyDialog:** banner and risk text come from `TransactionSemantics`. MySQL gets a red banner and a second checkbox mapped to `acceptNoRollback`. Typed-name case rule per engine. "Runs as" text comes from the provider.

---

## 8. Storage migration

`SchemaScopeStore.Initialise` gets versioned migrations using `PRAGMA user_version`. Each step is guarded by `pragma table_info`, so it is idempotent and safe on a partially migrated database.

- **v1:** today's schema, recorded as the baseline.
- **v2:**
  - New table **`engine_connections`**, holding **non-SQL Server connections only**. It has the `connections` columns plus `engine`, `port`, `ssl_mode` and `engine_options_json`. SQL Server rows stay byte-for-byte in `connections`.
  - `apply_runs.engine INTEGER NOT NULL DEFAULT 0`.
  - `apply_batches.statement_index INTEGER NULL`.

Rules for the split:
- **A single id space** across both tables. Ids are GUIDs, and insert checks uniqueness across both.
- `sort_order` is shared, so reorder works across both tables.
- `GetConnections()` returns the union ordered by `sort_order`. `GetConnections(DatabaseEngine)` filters by engine; QueryScope uses SqlServer.
- **Changing a saved connection's engine** is a delete plus insert across tables, and the stored secret is dropped (SecurityTests case).
- Shipped SchemaScope and QueryScope builds read only `connections`, so they can never send credentials over SqlClient to a PostgreSQL or MySQL row. After a downgrade those connections are hidden, not deleted; the release notes say so.

Other persisted formats:
- Enums (`DbObjectType`, `AuthMode`, `ObjectStatus`) are append-only.
- New JSON fields are optional and default to SqlServer.
- `DbObject.Key` in Upper mode is byte-identical, so history, shares and profiles keep matching.
- R0a adds round-trip tests on old CompareResult, share bundle, history and profile JSON fixtures.
- `DefinitionCache` is unchanged for SQL Server. If a non-SQL engine ever uses it, it moves to FormatVersion 2 with string keys, a string freshness stamp (e.g. a server-side md5 of `prosrc||proconfig`) and the engine in the path. v1 files then miss safely.

---

## 9. Test strategy

- **Regression nets first (R0a):**
  1. A LocalDB golden test that builds a fixed schema, reads it, and snapshots each `DbObject` Key/CompareText/Hash to checked-in JSON.
  2. A full-run `ScriptGenerator` digest with all sections plus destructive over the `ObjectFixture` catalogs.
  3. The existing `DestructiveSectionTests` digests, never updated.
- **New project `tests/SchemaScope.Engines.Tests`**, so the 337 tests are untouched in project, assembly and timing.
  - Attributes `[PostgresFact]` / `[MySqlFact]` check `SCHEMASCOPE_ENGINE_TESTS=1` **before** any Docker discovery. A plain `dotnet test`, including on the Windows CI and release.yml, skips them in milliseconds.
  - Collection fixtures start one container per engine per run with **Testcontainers on random host ports**. Images: `postgres:13/16/18`, `mysql:8.0/8.4`, `mariadb:10.11/11.4`.
  - **Endpoint guard:** the fixture refuses ports 5432 and 3306 on localhost and any server it did not start, unless `SCHEMASCOPE_ENGINE_CI=1` names a CI service container.
  - Each test creates its own `schemascope_test_<guid>_src/_tgt` databases (lower-case for PG). `Drop` is guarded by that prefix, mirroring `LocalDb.Drop`. The unguarded `DestructiveDb.Drop` pattern is not copied.
- **Scenarios mirroring the LocalDB demo,** in an engine-parameterised base:
  - six-status compare: Same, Formatting (function whitespace), Different, Missing, Extra, Unreadable;
  - case twins (`"Users"`/`users`, MySQL lctn=0);
  - PG overloads;
  - extension objects excluded;
  - generate, validate, apply over HTTP with write mode and typed name, then re-compare to 100% with the audit row carrying the engine;
  - safety: cancel during a lock wait, write mode off mid-file, the check before commit, losing the audit log stops the run, null InspectAsync refused, Protected host refused (including `localhost:5432` and multi-host);
  - MySQL: file 3 of 5 fails, files 1–2 stay applied, audit shows partial with the right index, regenerate contains only the remainder, no apply without `acceptNoRollback`.
- **Offline (no Docker, normal suite):**
  - dialect lexers ($$, E'', nested comments; backticks, #, backslash);
  - key folds and BuildIndex suffixing;
  - type-map matrix plus the H1–H7 honesty tests;
  - generators fed through a `CatalogOverride`-style snapshot override;
  - splitter parity between validator and `Verify`;
  - fake `psql.cmd` / `mysql.cmd` runner-order tests (same technique as `RunnerOrderTests`).
- **Cross-engine:** CI uses in-memory snapshots for one side. A local-only `[Fact]` runs LocalDB against a PG container, gated on both env vars.
- **CI:** a new `engines` job on **ubuntu-latest**, because Windows runners cannot run Linux containers. It runs only the engine project. The Windows job is unchanged. Engine tests must not touch Windows-only paths: DPAPI stores with saved passwords, netsh.

---

## 10. Packaging

- The Shell is `PublishSingleFile + SelfContained + IncludeNativeLibrariesForSelfExtract`, **untrimmed**. Trimming is not an option because WinForms and reflection-based JSON block it, so full driver IL ships in compressed form.
- Expected growth:
  - Npgsql: about +0.5 MB compressed, fully managed.
  - MySqlConnector: about +0.4 MB, fully managed. Do not use the GPL `MySql.Data`.
  - Their main dependency (Logging.Abstractions) is already in the bundled ASP.NET framework.
  - Do not reference Npgsql's optional plugins (NetTopologySuite, Json.NET).
- **pgsqlparser / libpg_query** (native DLL, BSD-3): adopt only after an R2 week-1 spike shows ≤ 4 MB compressed and clean self-extraction. Otherwise PG validation is "lexical only". If the native library fails to load at runtime, fall back to lexical and say so.
- **Oracle** ODP.NET would add about 4–5 MB under the Oracle Free Use Terms (not OSI-approved), so it is deferred.
- `build.ps1` gets a **size budget** that fails above roughly 70 MB (today 65.8 MB).
- Drivers live only in provider projects referenced by Api, never by Core or SqlServer, so `QueryScope.exe`, its release and its winget/scoop hashes are unchanged.
- README, release body and winget/scoop descriptions are updated per release ("SQL Server, PostgreSQL, MySQL…").

---

## 11. Phased roadmap

Every release ships on its own and is gated on the server by the capability matrix.

### R0a: Security and storage groundwork (1.3.x, invisible), ~1–1.5 weeks
- **Scope:**
  - Golden nets (§9).
  - `DatabaseEngine` and Engine fields everywhere, defaulting to SqlServer. `ScriptEndpoint` trailing parameter. `Port`/`SslMode`.
  - Merged SameDestination/TryRehydrate with engine, port and parsed hosts.
  - Store v2 migration with the empty `engine_connections` table. QueryScope reads SQL Server connections only.
  - `DbException` catch in ApplyService.
  - Fail-closed SavedServer (unparseable means Protected).
  - Broadened scrubber keys. URI strings refused. Inline SourceRef goes through TryRehydrate.
  - BuildIndex duplicate suffix plus warning.
  - Old-JSON round-trip tests.
- **Done when:**
  - 337 tests pass with no assertion edits, and the digests plus goldens are unchanged.
  - New Security, StoreMigration and JSON-compatibility tests are green.
  - A manual LocalDB smoke test (compare, scripts, apply) passes.

### R0b: Provider seam with SQL Server only (1.3.x), ~1.5 weeks
- **Scope:**
  - Core contracts (§2.3), `EngineRegistry`, `SqlServerEngineProvider`/`Dialect`/`Identity`.
  - `ParseEndpoint` replaces `SqlConnectionStringBuilder` in SavedServer/LoginOf/Endpoint/IsSource.
  - CompareRunner reader-factory overload. CompareService uses the registry, with a legacy constructor kept for `Shares.NewCompares`.
  - `SqlServerApplyExecutor` with static forwarders.
  - `IScriptGeneratorFactory` with `SqlServerScriptGenerator`.
  - Extraction of the `Scripting/Planning` helpers with forwarders.
  - `GET /api/engines` (SQL Server only).
  - Viewer DI test.
  - UI driven by the engine descriptor; the engine select is hidden while only one engine exists.
- **Done when:**
  - Everything from R0a still holds.
  - OwnerRouteTests lists `/api/engines`.
  - The exe is within 100 KB of the R0a size, and QueryScope.exe is unchanged.

### R1: PostgreSQL ↔ PostgreSQL compare, read-only, Preview (1.4), ~3 weeks
- **Scope:**
  - `SchemaScope.PostgreSql`: connection service, test, browse via `postgres`.
  - `PgSchemaReader` over pg_catalog, READ ONLY with statement_timeout, version-branched for PG 13–18. Excludes system schemas and extension members (`pg_depend deptype='e'`).
  - Objects read: schemas, tables, columns (`format_type`), defaults, identity and generated columns, constraints (`pg_get_constraintdef`), indexes (`pg_get_indexdef` plus structured columns), views (`pg_get_viewdef`), functions and procedures (`pg_get_functiondef`; aggregates from `pg_aggregate`), triggers, sequences, domain/enum/composite types, materialized views, extensions.
  - `Fold=Exact` plus a Signature.
  - `PgDialect`: no deep parse at first; canonical writer with `"` quoting.
  - Dialect-aware StructureDiff facets. Excluded-schema defaults from the dialect. Engine test project and ubuntu CI job.
- **Done when:**
  - PG 13/16/18 containers pass the six-status scenario. Case twins and overloads do not collide. Extension objects are excluded.
  - Results agree with a `pg_dump --schema-only` reference on the fixture.
  - Script and apply routes return 400 for PG.
  - The exe grows ≤ 1 MB.

### R2: PostgreSQL scripts to a folder (1.5), ~4 weeks (+1 if the parser is adopted)
- **Scope:** `PgScriptGenerator` on snapshots, reusing ScriptWriter, ReviewLog, the Planning helpers and the numbered folders.
  - **01:** add a column only when it is nullable or has a default.
  - **03:** schemas, enum/domain types, sequences and tables; views and functions copied from `pg_get_*def` in `pg_depend` order and marked "not compile-checked"; extensions are noted only.
  - **04:** widen only, held when a view depends on the column.
  - **05:** constraint names per table; FK/CHECK added `NOT VALID`, then `VALIDATE` in a later file.
  - **06:** no CONCURRENTLY.
  - **07:** trigger together with its function.
  - **10:** `CREATE OR REPLACE` when the signature is unchanged, otherwise held.
  - **99:** review.
  - 02, 08 and 09 report not applicable.
  - Also: PG probes with NotApplicable vs NotChecked, `PgStatementSplitter`, `PgScriptValidator` with ValidationLevel, psql runner, ObjectScripts for PG (R2b).
- **Done when:**
  - Container test: generate, validate, apply with psql inside the container, re-compare reaches 100% for items that were not held, and every held item has a reason.
  - The splitter parity test passes.
  - SQL Server digests are unchanged and the size budget passes.

### R3: PostgreSQL Write mode (1.6), ~2 weeks (+R3b destructive, ~1 week)
- **Scope:** `PgApplyExecutor` (§5.3); engine-aware gates; PG identity with a non-superuser test; ApplyDialog wording.
- **Done when:**
  - The ported ApplySafety and ApplyIntegration scenarios pass on PG 16/18.
  - Protected cases pass: `localhost:5432`, multi-host, unparseable, and SQL Server protecting PG on the same host.
  - SQL Server apply tests are unchanged.
  - The security checklist (TryRehydrate, Protected, scrubber, audit) is signed off.

### R4: Cross-engine compare, SQL Server ↔ PostgreSQL (2.0), ~4 weeks
- **Scope:** `LogicalType` filled by both readers, `ITypeMap` matrix, SchemaMap, `CrossEngineMatcher` (ambiguity handling, system-named keys matched by column set), `CrossEngineClassifier`, the two appended statuses, honesty tests H1–H7, and the UI changes from §7.
- **Done when:**
  - A SQL Server fixture and its hand-ported PG twin give the expected mix: int/bigint/nvarchar/bit read Same or Equivalent, `datetime2(7)` reads Different (lossy), procedures read NotComparable, synonyms show "no equivalent".
  - The property test passes: nothing lossy is Same or Equivalent.
  - Old history and shares load.
  - The local live LocalDB-vs-PG test passes.

### R4b: Cross-engine structural scripts to a folder (2.1), ~2 weeks
- **Scope:** §4.6.
- **Done when:**
  - Offline: every module is held, 11 is refused, lossy columns are held, ambiguous rows are never created.
  - Container: generated 01/03/05/06 applied with psql, and a cross-engine re-compare shows only Same, Equivalent, NotComparable or held items, with no Different on the generated tables.
  - Apply returns 400.

### R5: MySQL 8.x / MariaDB 10.11+ compare and scripts to a folder (2.2), ~4 weeks
- **Scope:**
  - MySqlConnector provider. Database = schema. Fold follows `@@lower_case_table_names`.
  - Reads `information_schema` plus `SHOW CREATE`, normalising `AUTO_INCREMENT=`, `DEFINER`, int display width, MariaDB default quoting and the hidden `json_valid` CHECK. A NULL routine definition maps to Unavailable. Event type.
  - MySQL dialect lexer.
  - Generator: 01/03/04/05/06/99, one statement per file.
  - Lexical validation, mysql runner.
  - MySQL added to the type map, so cross-engine compare with SQL Server and PG works.
- **Done when:**
  - Six statuses pass on mysql 8.0/8.4 and mariadb 10.11/11.4.
  - Case-distinct tables stay distinct with lctn=0.
  - Scripts converge with the mysql client.
  - Apply returns 400 and the size budget passes.

### R6: MySQL / MariaDB Write mode (2.3), ~2.5 weeks
- **Scope:** §5.4.
- **Done when:**
  - The partial-failure, resume-by-regenerate, no-acknowledgement, stop-between-statements and write-mode-off scenarios pass.
  - The "rolled back" wording test passes.
  - SQL Server and PG apply suites are unchanged.

### Later, on demand: SQLite, Oracle
Each one goes through the same ladder (compare, then scripts, then apply) and passes through the same gates. Estimate 4–6 weeks each.

**Total to R6: roughly 7–8 months of focused work.** Each release is useful by itself.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Secret released across engine or port (e.g. a SQL Server password sent to a PG endpoint on the same host) | One merged SameDestination with engine + port + parsed hosts; drop the secret on engine change; tests in R0a before any selector ships |
| Protected-server fail-open (`host:5432`, Npgsql raw strings, multi-host) | Provider ParseEndpoint; unparseable means Protected; host-level match across engines; null identity refuses on new engines |
| Old builds reading new rows and sending credentials over TDS | Separate `engine_connections` table |
| Silent object loss from case folding or overloads | Per-dialect fold; Signature in the key; BuildIndex keeps both with a suffix and a warning; ambiguity marked as Different, never Missing |
| SQL Server output regresses | Generator is wrapped, not refactored; golden nets plus existing digests gate every PR |
| False "rolled back" promise on MySQL | One statement per file enforced by Verify; AcceptNoRollback; wording test; no swaps or destructive scripts |
| Cross-engine results look broken or dishonest | Structural compare on logical types; Lossy counts as Different; NotComparable for bodies; H1–H7 tests |
| Catalog drift (PG 18 NOT NULL rows / `conenforced` / virtual columns; MySQL 8.0.13/.16/.19/.23; MariaDB defaults) | Detect version at connect; branched queries; container version matrix |
| Validation over-claimed for engines without a parser | ValidationLevel shown in UI, README and Write mode; Passed never claimed |
| Touching the user's real local PG and MySQL | Random-port containers only; endpoint guard; env gate checked before Docker discovery; no default connection strings pointing at localhost |
| PG data leaking through `Detail` in exceptions | Strip Detail/Where/Hint and truncate before reviews, messages, audit and shares |
| Locks on production during apply | `SET LOCAL lock_timeout` / `lock_wait_timeout`; widen-only type changes; no in-app dry run |
| Exe size and native DLL issues | Drivers only in provider projects; size budget; parser adopted only after a spike |
| Scope creep | One engine and one capability per release; cross-engine apply and destructive out of scope |
| PG identity function needs privileges a deploy login lacks | Fallback chain plus a non-superuser container test; decision D7 |

---

## 13. Open decisions for the founder

| # | Decision | Recommendation |
|---|---|---|
| D1 | Engines and order | **PostgreSQL first** (compare, then scripts, then apply), then MySQL 8.x with MariaDB 10.11+ as a tested flavour of the same provider. |
| D2 | Cross-engine compare before or after MySQL? | **Before** (R4, after PG apply). It is the headline feature, and it is only honest once two readers are mature. |
| D3 | In-app Write mode on MySQL/MariaDB, where DDL cannot be rolled back | **Yes, but last (R6)**, with one statement per file, a server-enforced no-rollback acknowledgement and resume by re-compare. Scripts-to-folder ships earlier in R5. |
| D4 | Cross-engine scripts appliable in-app? | **No, folder only.** Cross-engine `11_destructive` is permanently ruled out. |
| D5 | How lossy mappings are classified | **Different with the reason** (strict, "never hide a real change"). Revisit only if migration users ask for it. |
| D6 | Add statuses Equivalent and NotComparable? | **Yes** (appended values 6 and 7). Older share viewers show them as "?", which is acceptable. Folding them into the six existing statuses would be misleading. |
| D7 | Protected policy, and what to do when PG identity is unavailable | **A Protected connection protects every engine and port on that host.** If no server identity can be obtained for a non-SQL engine, **refuse the apply.** |
| D8 | Oracle and SQLite | **Deferred; build only if users ask.** Oracle as a separate download because of licence and size. SQLite has low value for a server compare tool. |
| D9 | Bundle native libpg_query for real PG validation? | **Spike in R2.** Adopt if it is ≤ 4 MB and self-extracts cleanly. Otherwise ship "lexical only – not validated" with honest labels. |
| D10 | QueryScope multi-engine | **Stays SQL Server only** for now. Drivers never go into Core or SqlServer, so this stays an option later. |
| D11 | Release labelling and downgrade | **R0a/R0b ship invisibly as 1.3.x. PG compare ships as "Preview" in 1.4 behind a setting. Cross-engine compare is 2.0.** Document that a downgrade hides non-SQL connections. |
| D12 | Exe size budget | **70 MB** (today 65.8 MB). |
| D13 | Linux CI job for container tests | **Yes, add ubuntu-latest.** The version matrix is PG 13/16/18, MySQL 8.0/8.4, MariaDB 10.11/11.4. Live LocalDB+Docker cross-engine tests stay local only. |
