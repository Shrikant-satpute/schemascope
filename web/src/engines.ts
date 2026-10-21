import { useEffect, useState } from 'react'
import { api } from './api'
import type { AuthMode, ConnectionSettings, DatabaseEngine, EngineDescriptor } from './types'

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

/** Fetches the catalogue once per page load; a failure keeps the built-in SQL Server entry. */
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

const PreviewKey = 'schemascope.showPreviewEngines'

/** Settings › "Show preview engines": off unless the user turned it on on this PC. */
export function showPreviewEngines(): boolean {
  try {
    return window.localStorage.getItem(PreviewKey) === '1'
  } catch {
    return false
  }
}

export function setShowPreviewEngines(on: boolean) {
  try {
    if (on) window.localStorage.setItem(PreviewKey, '1')
    else window.localStorage.removeItem(PreviewKey)
  } catch {
    /* storage blocked - the setting lasts until reload */
  }
}

/** The engines a new connection may pick: preview ones only with the setting on. */
export function selectableEngines(list: EngineDescriptor[], showPreview = showPreviewEngines()): EngineDescriptor[] {
  return list.filter((e) => !e.preview || showPreview)
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
