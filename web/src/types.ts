// Mirrors the C# contracts. Enums arrive as camelCase strings.

export type AuthMode =
  | 'windows'
  | 'sqlLogin'
  | 'azureAdPassword'
  | 'azureAdInteractive'
  | 'azureAdDefault'
  | 'password'
  | 'kerberos'
  | 'clientCertificate'

/** Append-only on the server; every connection saved before engines existed is sqlServer. */
export type DatabaseEngine = 'sqlServer' | 'postgreSql' | 'mySql' | 'mariaDb' | 'oracle' | 'sqlite'

/** TLS for PostgreSQL (and later MySQL). SQL Server keeps its Encrypt / Trust server certificate switches. */
export type SslMode = 'disable' | 'prefer' | 'require' | 'verifyCa' | 'verifyFull'

/** GET /api/engines/preview: Settings > "Show preview engines", which the server enforces. */
export interface PreviewEngines {
  enabled: boolean
  /** Display names of this build's preview engines, on or off. */
  engines: string[]
}

export type EngineFeature =
  | 'compare'
  | 'scripts'
  | 'objectScripts'
  | 'apply'
  | 'destructive'
  | 'crossCompare'
  | 'crossScripts'

export type TransactionSemantics = 'perFileTransactional' | 'noDdlRollback'

export interface EngineCapabilities {
  schemas: boolean
  sequences: boolean
  synonyms: boolean
  tableTypes: boolean
  aliasTypes: boolean
  filteredIndexes: boolean
  includeColumns: boolean
  routineOverloads: boolean
  byteExactModuleText: boolean
  ddl: TransactionSemantics
  maxIdentifierBytes: number
  objectTypes: string[]
  features: EngineFeature[]
  preview: boolean
}

export interface EngineAuthOption {
  mode: AuthMode
  label: string
  /** The few words a connection list shows. */
  short: string
  needsUser: boolean
}

export interface EngineFormText {
  serverLabel: string
  serverHint: string
  serverPlaceholder: string
  connectionStringHint: string
  connectionStringPlaceholder: string
  databasePlaceholder: string
  port: boolean
  sslMode: boolean
  encrypt: boolean
  trustServerCertificate: boolean
  readOnlyIntent: boolean
}

/** GET /api/engines: one engine as the UI renders it. Capabilities and wording only. */
export interface EngineDescriptor {
  engine: DatabaseEngine
  displayName: string
  /** For engine chips ("SQL", "PG"). */
  shortName: string
  preview: boolean
  defaultPort?: number | null
  browseDatabase?: string | null
  defaultSchema: string
  defaultExcludedSchemas: string[]
  monacoLanguage: string
  wordSeparators: string
  transactions: TransactionSemantics
  capabilities: EngineCapabilities
  auth: EngineAuthOption[]
  form: EngineFormText
  defaults: { auth: AuthMode; encrypt: boolean; trustServerCertificate: boolean; connectTimeoutSeconds: number }
  /** "Create scripts" wording for this engine; absent for SQL Server, whose wording is built in (scriptSections.ts). */
  scripts?: EngineScriptText | null
  /** "Apply to target" wording for this engine; absent for SQL Server, whose wording is built in (ApplyDialog). */
  apply?: EngineApplyText | null
}

export interface EngineApplyText {
  /** What a file's transaction promises, worded from the engine's transaction semantics. */
  transactions: string
  /** The typed database name must match letter for letter; the server checks the same rule. */
  typedNameExact: boolean
}

/** One "Create scripts" section as the dialog shows it for an engine. */
export interface EngineScriptSection {
  section: ScriptSection
  folder: string
  label: string
  hint: string
  warning?: string | null
  /** False: the engine has no such thing; shown greyed out and never sent. */
  applicable: boolean
}

export interface EngineScriptText {
  /** The client each folder's run_all.ps1 calls ("psql"). */
  runner: string
  /** What the offline check does on this build, in words. */
  validation: string
  /** True when the check uses the engine's own parser; false: lexical only, never "passed". */
  parsed: boolean
  sections: EngineScriptSection[]
}

export type ObjectStatus =
  | 'same'
  | 'formattingOnly'
  | 'different'
  | 'missingInTarget'
  | 'onlyInTarget'
  | 'unavailable'

export type DbObjectType =
  | 'schema'
  | 'table'
  | 'view'
  | 'storedProcedure'
  | 'scalarFunction'
  | 'tableValuedFunction'
  | 'aggregateFunction'
  | 'trigger'
  | 'userDefinedType'
  | 'tableType'
  | 'sequence'
  | 'synonym'
  | 'materializedView'
  | 'domain'
  | 'enumType'
  | 'compositeType'
  | 'extension'

export interface ConnectionSettings {
  id: string
  label: string
  /** Missing on rows from older builds: SQL Server. */
  engine?: DatabaseEngine
  server: string
  /** Null = the engine's default port. SQL Server keeps its port in the server name. */
  port?: number | null
  /** PostgreSQL: null = Verify full. */
  ssl?: SslMode | null
  database: string
  auth: AuthMode
  userName?: string | null
  password?: string | null
  encrypt: boolean
  trustServerCertificate: boolean
  connectTimeoutSeconds: number
  applicationIntentReadOnly: boolean
  useRawConnectionString: boolean
  rawConnectionString?: string | null
  protectedServer: boolean
  colour: string
  lastUsedUtc?: string | null
  displayName?: string
}

export interface ConnectionTestResult {
  success: boolean
  message?: string
  hint?: string
  elapsedMs: number
  productVersion?: string
  edition?: string
  serverName?: string
  databaseName?: string
  collation?: string
  objectCount: number
  canViewDefinitions: boolean
}

export interface DatabaseListItem {
  name: string
  hasAccess: boolean
}

export interface CompareOptions {
  ignoreComments: boolean
  ignoreCase: boolean
  useSmartParseCompare: boolean
  ignoreCollation: boolean
  ignoreFillFactor: boolean
  ignoreIndexPadding: boolean
  ignoreIdentitySeed: boolean
  ignoreSystemNamedConstraints: boolean
  ignoreColumnOrder: boolean
  ignoreNotForReplication: boolean
  ignoreFileGroups: boolean
  includedTypes: DbObjectType[]
  excludedSchemas: string[]
  excludedNamePatterns: string[]
  includeSystemObjects: boolean
  /** Which object types these options know about (the server's CompareOptions.TypeSet); kept as received. */
  typeSet?: number
}

/** One database as it took part in a past compare. */
export interface CompareHistoryDatabase {
  connectionId?: string | null
  label: string
  server: string
  database: string
}

export interface CompareHistoryEntry {
  id: string
  signature: string
  firstRunUtc: string
  lastRunUtc: string
  runCount: number
  source: CompareHistoryDatabase
  targets: CompareHistoryDatabase[]
  options: CompareOptions
  objectCount: number
  differenceCount: number
  lowestMatchPercent: number
  totalMs: number
}

export interface CompareProfile {
  id: string
  name: string
  options: CompareOptions
  createdUtc: string
}

export interface TargetCell {
  status: ObjectStatus
  addedLines: number
  removedLines: number
  changedLines: number
  note?: string
}

export interface CompareRow {
  key: string
  type: DbObjectType
  typeLabel: string
  schema: string
  name: string
  fullName: string
  parentName?: string
  /** What tells this object from others of its name: a PostgreSQL overload's argument types. */
  signature?: string
  cells: TargetCell[]
  worst: ObjectStatus
  hasDifference: boolean
  differingTargets: number
}

export interface TargetSummary {
  id: string
  label: string
  /** Missing on runs saved before engines existed: SQL Server. */
  engine?: DatabaseEngine
  server: string
  database: string
  productVersion: string
  collation: string
  same: number
  formattingOnly: number
  different: number
  missingInTarget: number
  onlyInTarget: number
  unavailable: number
  totalObjects: number
  totalDifferences: number
  matchPercent: number
  readMs: number
  lastObjectChangeUtc?: string | null
  error?: string
  failed: boolean
}

export interface SnapshotInfo {
  label: string
  engine?: DatabaseEngine
  server: string
  database: string
  productVersion: string
  collation: string
  objectCount: number
  capturedAtUtc: string
  readMs: number
}

export interface TypeBreakdown {
  type: DbObjectType
  label: string
  total: number
  withDifferences: number
  // Split of withDifferences by each row's worst status. Optional: snapshots
  // saved before it existed come back without it (or as zeros).
  different?: number
  formattingOnly?: number
  missingInTarget?: number
  onlyInTarget?: number
  unavailable?: number
}

export interface CompareResult {
  id: string
  startedUtc: string
  totalMs: number
  source: SnapshotInfo
  targets: TargetSummary[]
  rows: CompareRow[]
  byType: TypeBreakdown[]
  timings: Record<string, number>
  warnings: string[]
  totalRows: number
  rowsWithDifferences: number
  singleTargetDrift: number
}

export interface RunSummary {
  runId: string
  status: 'running' | 'done' | 'failed' | 'cancelled'
  startedUtc: string
  error?: string
  hint?: string
  result?: CompareResult
}

export interface ProgressEvent {
  type: 'progress' | 'done' | 'error'
  sourceId: string
  label: string
  stage: string
  percent: number
  detail?: string
  elapsedMs: number
}

export interface ColumnFacet {
  type: string
  nullable: string
  identity: string
  default: string
  computed: string
  collation: string
  ordinal: number
}

export interface StructureRow {
  name: string
  status: 'same' | 'changed' | 'missingInTarget' | 'onlyInTarget'
  leftText?: string
  rightText?: string
  left?: ColumnFacet
  right?: ColumnFacet
  changedFields: string[]
}

export interface StructureSection {
  title: string
  rows: StructureRow[]
  changedCount: number
}

export interface TargetObjectDetail {
  id: string
  label: string
  status: ObjectStatus
  exists: boolean
  text: string
  note?: string
  addedLines: number
  removedLines: number
  structure: StructureSection[]
}

export interface ObjectDetail {
  key: string
  typeLabel: string
  schema: string
  name: string
  fullName: string
  parentName?: string
  isModule: boolean
  hasStructure: boolean
  language: string
  sourceExists: boolean
  sourceText: string
  sourceLabel: string
  sourceModified?: string
  targets: TargetObjectDetail[]
}

export interface SourceRef {
  connectionId?: string
  database?: string
  label?: string
  inline?: Partial<ConnectionSettings>
}

export interface CompareRequest {
  source: SourceRef
  targets: SourceRef[]
  options?: CompareOptions
  profileId?: string
}

// ---------------- where is this used ----------------

export interface ReferenceLine {
  line: number
  column: number
  text: string
}

export interface ReferenceObject {
  key: string
  schema: string
  name: string
  typeLabel: string
  /** This object *is* the thing being searched for. */
  isDefinition: boolean
  matchCount: number
  lines: ReferenceLine[]
}

export interface ReferenceDatabase {
  /** "source", or a target's index as a string. */
  id: string
  label: string
  objectCount: number
  matchCount: number
  objects: ReferenceObject[]
}

export interface ReferenceResult {
  name: string
  mode: 'usedBy' | 'uses'
  databases: ReferenceDatabase[]
  truncated: boolean
  elapsedMs: number
}

/** One browser currently reading a shared link. We know its address, not who it is. */
export interface ShareViewer {
  ipAddress: string
  connectedUtc: string
}

/**
 * lan - a real network card, the one to send people
 * name - this computer's name, which only works if the network can resolve it
 * virtual - a Hyper-V or WSL adapter that exists only on this PC
 */
export type AddressKind = 'lan' | 'name' | 'virtual'

export interface ShareAddress {
  value: string
  kind: AddressKind
}

export interface ShareLink {
  url: string
  kind: AddressKind
}

export interface ShareSummary {
  token: string
  sourceLabel: string
  sourceDetail: string
  targetLabels: string[]
  comparedUtc: string
  createdUtc: string
  expiresUtc: string
  objectCount: number
  totalViews: number
  /** One per address this machine answers on, best first. Empty while sharing is off. */
  urls: ShareLink[]
  viewersNow: number
  viewers: ShareViewer[]
}

export interface ShareStatus {
  sharing: boolean
  port: number
  addresses: ShareAddress[]
  firewallAllowed: boolean
  firewallCommand: string
  error?: string | null
  shares: ShareSummary[]
}

/** What a shared link serves: the comparison, and when it was taken. */
export interface SharedComparison {
  token: string
  sourceLabel: string
  hostName: string
  comparedUtc: string
  sharedUtc: string
  expiresUtc: string
  result: CompareResult
}

export interface Health {
  ok: boolean
  version: string
  readOnly: boolean
  storePath: string
  secretsProtected: boolean
}

export interface ExportResult {
  success: boolean
  message?: string
  path?: string
  fileCount: number
}

/**
 * The folders "Create scripts" can write, in folder order. The server's
 * ScriptSection enum, serialised as camelCase strings.
 */
export type ScriptSection =
  | 'newColumns'
  | 'tableTypes'
  | 'missingObjects'
  | 'columnTypes'
  | 'constraints'
  | 'indexes'
  | 'triggers'
  | 'formatting'
  | 'differentModules'

export interface ScriptOptions {
  sections: ScriptSection[]
  /** Must be a full Windows path; the server writes a new dated folder inside it. */
  outputFolder: string
  quarantineFolder?: string
  /** `*` and `?` wildcards, matched against "schema.name" or "name". */
  excludePatterns: string[]
  checkExistingData: boolean
  largeTableRows: number
  probeTimeoutSeconds: number
  /**
   * Also write 11_destructive, the changes that can lose data. The server
   * refuses true unless Read & write is on for this session.
   */
  includeDestructive?: boolean
}

export interface ScriptRequest {
  /** Index into `CompareResult.targets` - not the tile's position on screen. */
  targetIndex: number
  options: ScriptOptions
  /** Made up by the caller, to poll `api.scripts.progress` while the run is going. */
  progressId?: string
}

/** How far a "Create scripts" run is: 0-100 and the step it is on. */
export interface ScriptProgress {
  percent: number
  step: string
}

export interface FolderResult {
  folder: string
  title: string
  files: number
  inRunAll: boolean
  hasRollback: boolean
  notes: string[]
}

export interface HeldBackItem {
  section: string
  object: string
  reason: string
}

export interface ScriptValidation {
  batches: number
  syntaxErrors: number
  modulesChecked: number
  modulesExact: number
  problems: string[]
  passed: boolean
  /** How far the check went, for engines other than SQL Server (absent: ScriptDom, as always). */
  level?: ValidationLevel | null
}

export interface ValidationLevel {
  /** Every statement went through the engine's own parser. */
  parsed: boolean
  /** Code bodies were proven to compile, not only to be the source's text. */
  moduleTextProven: boolean
  notValidatedReason?: string | null
  parser?: string | null
}

export interface ScriptOutcome {
  success: boolean
  message?: string
  /** The dated run folder that was written, even when the run stopped part way. */
  path?: string
  fileCount: number
  folders: FolderResult[]
  heldBack: HeldBackItem[]
  warnings: string[]
  validation: ScriptValidation
  elapsedMs: number
  /** Set only while Read & write is on and the scripts came out clean: what an apply refers to. */
  planId?: string | null
  /** The database an apply would change, from the compare session's own connection. */
  applyTarget?: ApplyTarget | null
  /** The folders an apply can run, in run order. */
  planSections?: PlanSection[]
}

// ---------------- one object's sync script ----------------

/** What the server will write for one object against one target. */
export type ObjectScriptPlan =
  | 'create'
  | 'alter'
  | 'tableChanges'
  | 'tableType'
  | 'trigger'
  | 'formatting'
  | 'extra'
  | 'identical'
  | 'unavailable'

/** Something in the comparison this object needs that the target lacks or has differently. */
export interface ObjectScriptDependency {
  /** The same row key as `CompareRow.key`. */
  key: string
  displayName: string
  type: string
  status: ObjectStatus
  /** A prerequisite (schema, alias type, sequence, synonym): always scripted with it. */
  required: boolean
  /** True when the CREATE itself would fail without it. */
  defaultIncluded: boolean
  reason: string
}

export interface ObjectScriptDependencies {
  key: string
  status: ObjectStatus
  plan: ObjectScriptPlan
  dependencies: ObjectScriptDependency[]
  warnings: string[]
}

export interface ObjectScriptRequest {
  /** Index into `CompareResult.targets`. */
  targetIndex: number
  /** `CompareRow.key`. */
  key: string
  /** Dependency keys to script as well, from the pre-check. */
  includeDependencies: string[]
  progressId?: string
}

export interface ObjectScriptPart {
  folder: string
  /** Absolute path of the written .sql file. */
  file: string
  objectName: string
  kind: string
}

export interface ObjectScriptOutcome {
  success: boolean
  message: string
  /** Every part in the order it runs, with "-- <folder>: <object>" separators. */
  script: string
  rollback: string | null
  /** Extras only: the DROP statement to copy. Generate script never runs it (Create scripts can put it in 11_destructive). */
  dropPreview: string | null
  parts: ObjectScriptPart[]
  heldBack: { objectName: string; reason: string }[]
  warnings: string[]
  validation: { passed: boolean; summary: string }
  dependenciesIncluded: string[]
  elapsedMs: number
  path: string
  /** Set only while Read & write is on and the scripts came out clean: what an apply refers to. */
  planId?: string | null
  /** The database an apply would change, from the compare session's own connection. */
  applyTarget?: ApplyTarget | null
  /** The folders an apply can run, in run order. */
  planSections?: PlanSection[]
}

// ---------------- write mode and apply ----------------

/** Read & write lives in server memory only: off on every launch, and off by itself after 30 minutes idle. */
export interface WriteModeState {
  enabled: boolean
  expiresUtc: string | null
}

export interface ApplyTarget {
  label: string
  /** Missing = SQL Server. */
  engine?: DatabaseEngine
  server: string
  /** Engines other than SQL Server (whose port is part of the server name). */
  port?: number | null
  database: string
  /** "Windows: DOMAIN\user", "SQL login: name" or "PostgreSQL user: name". */
  login: string
  protected: boolean
}

export type PlanRisk = 'normal' | 'changesCode' | 'destructive'

export interface PlanSection {
  /** "04_columntypes", ..., "11_destructive". */
  folder: string
  title: string
  files: number
  defaultSelected: boolean
  risk: PlanRisk
  warning: string | null
}

/** Everything the apply dialog needs from a script run that produced a plan. */
export interface ApplyPlan {
  runId: string
  targetIndex: number
  planId: string
  applyTarget: ApplyTarget
  planSections: PlanSection[]
  /** Items the generator held back. They are never applied. */
  heldBack: number
}

export interface ApplyRequest {
  planId: string
  targetIndex: number
  /** Folder names, a subset of the plan's. */
  sections: string[]
  confirmDatabase: string
  acceptDataLoss: boolean
  progressId: string
}

export interface AppliedFile {
  folder: string
  file: string
  objectName: string
  ok: boolean
  error: string | null
  elapsedMs: number
}

export interface ApplyResult {
  success: boolean
  message: string
  applied: AppliedFile[]
  /** The file the run stopped at, when it did not finish. */
  stoppedAt: string | null
  auditId: string
}
