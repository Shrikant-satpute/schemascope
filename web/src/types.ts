// Mirrors the C# contracts. Enums arrive as camelCase strings.

export type AuthMode =
  | 'windows'
  | 'sqlLogin'
  | 'azureAdPassword'
  | 'azureAdInteractive'
  | 'azureAdDefault'

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

export interface ConnectionSettings {
  id: string
  label: string
  server: string
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
  cells: TargetCell[]
  worst: ObjectStatus
  hasDifference: boolean
  differingTargets: number
}

export interface TargetSummary {
  id: string
  label: string
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
}
