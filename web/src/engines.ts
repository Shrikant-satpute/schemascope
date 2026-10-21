import { useEffect, useState } from 'react'
import { api } from './api'
import type { AuthMode, ConnectionSettings, DatabaseEngine, EngineDescriptor, EngineFeature, PreviewEngines } from './types'

/**
 * The engine catalogue: GET /api/engines, fetched once and cached. Every engine-specific word the connection
 * screens show (sign-in labels, placeholders, hints, which Advanced switches exist) comes from here, so a new
 * engine is a server change, not a hunt through components. The server enforces the capabilities whatever the
 * UI shows.
 *
 * Until the catalogue arrives (and if it cannot be fetched) the built-in SQL Server entry below is used. It is
 * the same wording the server sends, so the first render never changes when the answer lands.
 */
export const SQL_SERVER: EngineDescriptor = {
  engine: 'sqlServer',
  displayName: 'SQL Server',
  shortName: 'SQL',
  preview: false,
  defaultPort: 1433,
  browseDatabase: 'master',
  defaultSchema: 'dbo',
  defaultExcludedSchemas: [
    'sys', 'INFORMATION_SCHEMA', 'guest', 'db_owner', 'db_accessadmin', 'db_securityadmin', 'db_ddladmin',
    'db_backupoperator', 'db_datareader', 'db_datawriter', 'db_denydatareader', 'db_denydatawriter',
  ],
  monacoLanguage: 'sql',
  wordSeparators: '`~!%^&*()-=+[{]}\\|;:\'",.<>/?',
  transactions: 'perFileTransactional',
  capabilities: {
    schemas: true,
    sequences: true,
    synonyms: true,
    tableTypes: true,
    aliasTypes: true,
    filteredIndexes: true,
    includeColumns: true,
    routineOverloads: false,
    byteExactModuleText: true,
    ddl: 'perFileTransactional',
    maxIdentifierBytes: 256,
    objectTypes: [],
    features: ['compare', 'scripts', 'objectScripts', 'apply', 'destructive'],
    preview: false,
  },
  auth: [
    { mode: 'windows', label: 'Windows (current user)', short: 'Windows auth', needsUser: false },
    { mode: 'sqlLogin', label: 'SQL login', short: 'SQL login', needsUser: true },
    { mode: 'azureAdPassword', label: 'Microsoft Entra - password', short: 'Entra password', needsUser: true },
    { mode: 'azureAdInteractive', label: 'Microsoft Entra - interactive (MFA)', short: 'Entra interactive', needsUser: false },
    { mode: 'azureAdDefault', label: 'Microsoft Entra - default', short: 'Entra default', needsUser: false },
  ],
  form: {
    serverLabel: 'Server name',
    serverHint: 'host, host\\instance or host,port',
    serverPlaceholder: 'localhost  or  10.0.0.5,1433',
    connectionStringHint: 'Anything Microsoft.Data.SqlClient accepts.',
    connectionStringPlaceholder: 'Server=10.0.0.5,1433;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true',
    databasePlaceholder: 'Platform_Core',
    port: false,
    sslMode: false,
    encrypt: true,
    trustServerCertificate: true,
    readOnlyIntent: true,
  },
  defaults: { auth: 'windows', encrypt: false, trustServerCertificate: true, connectTimeoutSeconds: 15 },
}

let cache: EngineDescriptor[] = [SQL_SERVER]
let loading: Promise<void> | null = null
const listeners = new Set<(list: EngineDescriptor[]) => void>()

/** Fetches the catalogue once per page load (and again when the preview switch changes); a failure keeps what we had. */
function load(): Promise<void> {
  loading ??= api
    .engines()
    .then((list) => {
      if (list.length > 0) cache = list
      listeners.forEach((l) => l(cache))
    })
    .catch(() => {
      /* keep the built-in entry; the server still decides what is allowed */
    })
  return loading
}

/** Every engine this build offers, in engine order. */
export function useEngines(): EngineDescriptor[] {
  const [list, setList] = useState(cache)
  useEffect(() => {
    listeners.add(setList)
    void load()
    return () => {
      listeners.delete(setList)
    }
  }, [])
  return list
}

let preview: PreviewEngines = { enabled: false, engines: [] }
let previewLoading: Promise<void> | null = null
const previewListeners = new Set<(p: PreviewEngines) => void>()

function loadPreview(): Promise<void> {
  previewLoading ??= api.previewEngines
    .get()
    .then((p) => {
      preview = p
      previewListeners.forEach((l) => l(preview))
    })
    .catch(() => {
      /* off: a preview engine is never offered on a guess */
    })
  return previewLoading
}

/**
 * Settings > "Show preview engines": the server's switch, kept in its store. While it is off the server does not
 * describe, test, save or compare a preview engine at all, so this only ever mirrors what the server enforces.
 * Turning it on or off re-reads the engine catalogue.
 */
export function usePreviewEngines(): [PreviewEngines, (on: boolean) => Promise<void>] {
  const [state, setState] = useState(preview)
  useEffect(() => {
    previewListeners.add(setState)
    void loadPreview()
    return () => {
      previewListeners.delete(setState)
    }
  }, [])
  const set = async (on: boolean) => {
    preview = await api.previewEngines.set(on)
    previewListeners.forEach((l) => l(preview))
    loading = null
    await load()
  }
  return [state, set]
}

/** The engines a new connection may pick: preview ones only with the setting on (the server lists them only then). */
export function selectableEngines(list: EngineDescriptor[], showPreview = preview.enabled): EngineDescriptor[] {
  return list.filter((e) => !e.preview || showPreview)
}

/** Whether the server offers a feature for an engine (missing = SQL Server); false when the engine is not turned on. */
export function supports(list: EngineDescriptor[], engine: DatabaseEngine | undefined | null, feature: EngineFeature): boolean {
  const want = engine ?? 'sqlServer'
  return list.find((e) => e.engine === want)?.capabilities.features.includes(feature) ?? (want === 'sqlServer')
}

/** Chip names even for an engine the catalogue does not list right now (a preview engine switched off). */
const SHORT_NAMES: Record<DatabaseEngine, string> = {
  sqlServer: 'SQL',
  postgreSql: 'PG',
  mySql: 'MySQL',
  mariaDb: 'MariaDB',
  oracle: 'Oracle',
  sqlite: 'SQLite',
}

const NAMES: Record<DatabaseEngine, string> = {
  sqlServer: 'SQL Server',
  postgreSql: 'PostgreSQL',
  mySql: 'MySQL',
  mariaDb: 'MariaDB',
  oracle: 'Oracle',
  sqlite: 'SQLite',
}

export function engineName(engine: DatabaseEngine | undefined | null): string {
  return NAMES[engine ?? 'sqlServer'] ?? String(engine)
}

/** SQL Server's major version as its release year. */
const SQL_YEARS: Record<string, string> = { '11': '2012', '12': '2014', '13': '2016', '14': '2017', '15': '2019', '16': '2022', '17': '2025' }

/**
 * An engine chip: "PG 16", "SQL 2022", or just "PG" / "SQL" without a version. The version is the product
 * version a compare or connection test reported ("16.4", "16.0.4135.4", "PostgreSQL 16.4").
 */
export function engineChip(engine: DatabaseEngine | undefined | null, version?: string | null): string {
  const e = engine ?? 'sqlServer'
  const short = SHORT_NAMES[e] ?? String(e)
  const major = (version ?? '').match(/(\d+)\./)?.[1] ?? (version ?? '').match(/^\D*(\d+)$/)?.[1]
  if (!major) return short
  return e === 'sqlServer' ? `${short} ${SQL_YEARS[major] ?? major}` : `${short} ${major}`
}

/** The descriptor for a connection's engine (missing = SQL Server); the built-in SQL Server entry when the catalogue has none. */
export function engineOf(list: EngineDescriptor[], engine: DatabaseEngine | undefined | null): EngineDescriptor {
  const want = engine ?? 'sqlServer'
  return list.find((e) => e.engine === want) ?? SQL_SERVER
}

/** The sign-in label of a saved connection, long ("Windows (current user)") or short ("Windows auth"). */
export function authText(list: EngineDescriptor[], c: Pick<ConnectionSettings, 'engine' | 'auth'>, short = false): string {
  const option = engineOf(list, c.engine).auth.find((a) => a.mode === c.auth)
  return option ? (short ? option.short : option.label) : c.auth
}

/** Whether this sign-in method takes a user name and password. */
export function needsUser(e: EngineDescriptor, auth: AuthMode | undefined): boolean {
  return e.auth.find((a) => a.mode === auth)?.needsUser ?? false
}

/** A new, empty connection for one engine. */
export function blankConnection(e: EngineDescriptor = SQL_SERVER): Partial<ConnectionSettings> {
  return {
    label: '',
    // left out for SQL Server, so a new connection posts exactly what it did before engines existed
    ...(e.engine === 'sqlServer' ? {} : { engine: e.engine }),
    // TLS is verified unless the user opts out (Trust server certificate)
    ...(e.form.sslMode ? { ssl: 'verifyFull' as const } : {}),
    server: '',
    database: '',
    auth: e.defaults.auth,
    userName: '',
    password: '',
    encrypt: e.defaults.encrypt,
    trustServerCertificate: e.defaults.trustServerCertificate,
    connectTimeoutSeconds: e.defaults.connectTimeoutSeconds,
    applicationIntentReadOnly: false,
    useRawConnectionString: false,
    rawConnectionString: '',
    protectedServer: false,
    colour: '',
  }
}
