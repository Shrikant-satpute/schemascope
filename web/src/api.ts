import type {
  ApplyRequest,
  ApplyResult,
  CompareHistoryEntry,
  CompareProfile,
  CompareRequest,
  ConnectionSettings,
  ConnectionTestResult,
  DatabaseListItem,
  EngineDescriptor,
  ExportResult,
  Health,
  ObjectDetail,
  ObjectScriptDependencies,
  ObjectScriptOutcome,
  ObjectScriptRequest,
  ProgressEvent,
  ReferenceResult,
  RunSummary,
  ScriptOutcome,
  ScriptProgress,
  ScriptRequest,
  CompareOptions,
  ShareStatus,
  ShareSummary,
  SharedComparison,
  WriteModeState,
} from './types'

const base = ''

/**
 * The desktop shell hands the API token over in the launch URL. It is lifted
 * into memory once and wiped from the address bar, so it never ends up in a
 * copied link or in anything the page later navigates to.
 *
 * It is also kept in sessionStorage, so a reload (F5, Ctrl+R) of the window
 * still has it. That storage belongs to this loopback origin and this window
 * alone, and the token dies with the process that minted it.
 *
 * In development there is no token here: the Vite proxy adds the header to
 * everything it forwards instead.
 */
const TokenKey = 'schemascope.k'

const token = (() => {
  let found: string | null = null
  try {
    found = new URLSearchParams(window.location.search).get('k')
    if (found) window.history.replaceState({}, '', window.location.pathname)
  } catch {
    /* no usable location */
  }
  try {
    if (found) window.sessionStorage.setItem(TokenKey, found)
    else found = window.sessionStorage.getItem(TokenKey)
  } catch {
    /* storage blocked - the token still lives in memory until a reload */
  }
  return found
})()

const authHeaders: Record<string, string> = token ? { 'X-SchemaScope-Token': token } : {}

/** EventSource cannot set headers, so SSE carries the token in the query instead. */
function withToken(path: string): string {
  if (!token) return path
  return `${path}${path.includes('?') ? '&' : '?'}k=${encodeURIComponent(token)}`
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(base + path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...authHeaders, ...init?.headers },
  })
  return read<T>(res)
}

/**
 * Requests for the shared-link viewer. That page is served to other machines
 * by the share listener, so these never carry the loopback token, whatever is
 * in memory. Keep every api.view.* call on this path.
 */
async function viewReq<T>(path: string): Promise<T> {
  const res = await fetch(base + path, { headers: { Accept: 'application/json' } })
  return read<T>(res)
}

async function read<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const body = await res.json()
      if (body?.message) message = body.message
      else if (typeof body?.error === 'string') message = body.error
    } catch {
      /* response was not json */
    }
    throw new Error(message)
  }

  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  health: () => req<Health>('/api/health'),

  /** What each engine of this build offers (cached by engines.ts). */
  engines: () => req<EngineDescriptor[]>('/api/engines'),

  connections: {
    list: () => req<ConnectionSettings[]>('/api/connections'),
    save: (c: Partial<ConnectionSettings>) =>
      req<ConnectionSettings>('/api/connections', { method: 'POST', body: JSON.stringify(c) }),
    remove: (id: string) => req<void>(`/api/connections/${id}`, { method: 'DELETE' }),
    reorder: (ids: string[]) =>
      req<void>('/api/connections/reorder', { method: 'POST', body: JSON.stringify(ids) }),
    test: (c: Partial<ConnectionSettings>) =>
      req<ConnectionTestResult>('/api/connections/test', { method: 'POST', body: JSON.stringify(c) }),
    databases: (c: Partial<ConnectionSettings>) =>
      req<DatabaseListItem[]>('/api/connections/databases', {
        method: 'POST',
        body: JSON.stringify(c),
      }),
  },

  history: {
    list: () => req<CompareHistoryEntry[]>('/api/history'),
    remove: (id: string) => req<void>(`/api/history/${id}`, { method: 'DELETE' }),
    clear: () => req<void>('/api/history', { method: 'DELETE' }),
  },

  /** The owner's side of sharing. Loopback only - the network cannot reach any of this. */
  shares: {
    status: () => req<ShareStatus>('/api/shares'),
    publish: (runId: string) =>
      req<{ share: ShareSummary; started: boolean; error?: string | null }>(
        `/api/compare/${runId}/share`,
        { method: 'POST' },
      ),
    revoke: (token: string) => req<void>(`/api/shares/${token}`, { method: 'DELETE' }),
    start: () => req<{ started: boolean; error?: string | null }>('/api/shares/start', { method: 'POST' }),
    stop: () => req<void>('/api/shares/stop', { method: 'POST' }),
    firewall: () => req<{ ok: boolean; message: string }>('/api/shares/firewall', { method: 'POST' }),
    removeFirewall: () =>
      req<{ ok: boolean; message: string }>('/api/shares/firewall', { method: 'DELETE' }),
  },

  /** What a shared link can ask for. Read only, and nothing else exists on that port. */
  view: {
    load: (token: string) => viewReq<SharedComparison>(`/api/view/${token}`),
    object: (token: string, key: string) =>
      viewReq<ObjectDetail>(`/api/view/${token}/object?key=${encodeURIComponent(key)}`),

    references: (token: string, name: string) =>
      viewReq<ReferenceResult>(`/api/view/${token}/references?name=${encodeURIComponent(name)}`),

    uses: (token: string, key: string) =>
      viewReq<ReferenceResult>(`/api/view/${token}/uses?key=${encodeURIComponent(key)}`),

    /** Held open while the page is open, so the Share tab knows who is reading. */
    live: (token: string) => {
      const es = new EventSource(`${base}/api/view/${token}/live`)
      return () => es.close()
    },
  },

  profiles: {
    list: () => req<CompareProfile[]>('/api/profiles'),
    save: (p: CompareProfile) =>
      req<CompareProfile>('/api/profiles', { method: 'POST', body: JSON.stringify(p) }),
    remove: (id: string) => req<void>(`/api/profiles/${id}`, { method: 'DELETE' }),
  },

  defaultOptions: () => req<CompareOptions>('/api/options/defaults'),

  /**
   * Read & write for this session. The server keeps it in memory only: it is
   * off on every launch and turns itself off after 30 minutes idle.
   */
  writeMode: {
    get: () => req<WriteModeState>('/api/write-mode'),
    enable: () =>
      req<WriteModeState>('/api/write-mode', {
        method: 'POST',
        body: JSON.stringify({ enabled: true, confirm: 'ENABLE' }),
      }),
    disable: () => req<WriteModeState>('/api/write-mode', { method: 'DELETE' }),
  },

  apply: {
    /**
     * Stops a running apply after the file it is on. Earlier files stay applied.
     * With `now`, the file it is on is cancelled as well and rolled back.
     */
    stop: (progressId: string, now = false) =>
      req<void>(`/api/apply/${encodeURIComponent(progressId)}/stop${now ? '?now=true' : ''}`, { method: 'POST' }),
  },

  scripts: {
    /** The folder the last "Create scripts" run wrote into, to prefill the next one. */
    lastFolder: () => req<{ path?: string | null }>('/api/scripts/last-folder'),
    /** How far the run started with this `progressId` is. Fails (404) before it starts and after it ends. */
    progress: (progressId: string) =>
      req<ScriptProgress>(`/api/scripts/progress/${encodeURIComponent(progressId)}`),
  },

  compare: {
    start: (request: CompareRequest) =>
      req<{ runId: string }>('/api/compare', { method: 'POST', body: JSON.stringify(request) }),
    get: (runId: string) => req<RunSummary>(`/api/compare/${runId}`),
    cancel: (runId: string) => req<void>(`/api/compare/${runId}/cancel`, { method: 'POST' }),
    object: (runId: string, key: string) =>
      req<ObjectDetail>(`/api/compare/${runId}/object?key=${encodeURIComponent(key)}`),

    /** Where is this name used, across every database in the run. */
    references: (runId: string, name: string) =>
      req<ReferenceResult>(`/api/compare/${runId}/references?name=${encodeURIComponent(name)}`),

    /** The other direction: what does this object call. */
    uses: (runId: string, key: string) =>
      req<ReferenceResult>(`/api/compare/${runId}/uses?key=${encodeURIComponent(key)}`),

    /** Subscribes to live progress. Returns an unsubscribe function. */
    events: (runId: string, onEvent: (e: ProgressEvent) => void) => {
      const es = new EventSource(withToken(`${base}/api/compare/${runId}/events`))
      es.onmessage = (m) => {
        try {
          onEvent(JSON.parse(m.data) as ProgressEvent)
        } catch {
          /* ignore malformed frame */
        }
      }
      es.onerror = () => es.close()
      return () => es.close()
    },

    /**
     * Writes the numbered script folders that make one target look like the
     * source. Writing them only reads the databases; a failed run still comes
     * back as 200 with `success: false` and the folder it got as far as.
     * Aborting the signal stops the run on the server too.
     */
    scripts: (runId: string, body: ScriptRequest, signal?: AbortSignal) =>
      req<ScriptOutcome>(`/api/compare/${runId}/scripts`, {
        method: 'POST',
        body: JSON.stringify(body),
        signal,
      }),

    /**
     * What one object needs before it can be scripted on its own against one
     * target: the plan, and the objects it depends on that the target lacks.
     */
    objectScriptDependencies: (runId: string, key: string, targetIndex: number) =>
      req<ObjectScriptDependencies>(
        `/api/compare/${runId}/object-script/dependencies?key=${encodeURIComponent(key)}&targetIndex=${targetIndex}`,
      ),

    /**
     * Writes the sync script for just this object (and the chosen
     * dependencies) and returns its text. Like `scripts`, generating runs
     * nothing against a database. Aborting the signal stops it on the server too.
     */
    objectScript: (runId: string, body: ObjectScriptRequest, signal?: AbortSignal) =>
      req<ObjectScriptOutcome>(`/api/compare/${runId}/object-script`, {
        method: 'POST',
        body: JSON.stringify(body),
        signal,
      }),

    /**
     * Runs a plan's files against the target, each in its own transaction,
     * stopping at the first error. Deliberately takes no abort signal: closing
     * or navigating must not cut a file off half way - `api.apply.stop` stops
     * it after the current file. Progress is on `api.scripts.progress`.
     */
    apply: (runId: string, body: ApplyRequest) =>
      req<ApplyResult>(`/api/compare/${runId}/apply`, { method: 'POST', body: JSON.stringify(body) }),

    /** Downloads html / csv / md, or writes .sql files and returns the result. */
    exportTo: async (
      runId: string,
      format: 'html' | 'csv' | 'md' | 'sql',
      body: Record<string, unknown> = {},
    ): Promise<ExportResult | void> => {
      const res = await fetch(`${base}/api/compare/${runId}/export`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders },
        body: JSON.stringify({ format, differencesOnly: true, keys: [], ...body }),
      })
      if (!res.ok) throw new Error(`Export failed: ${res.status}`)

      if (format === 'sql') return (await res.json()) as ExportResult

      const blob = await res.blob()
      const disposition = res.headers.get('content-disposition') ?? ''
      const match = /filename[^;=\n]*=(?:(\\?['"])(.*?)\1|(?:[^\s]*'.*?')?([^;\n]*))/.exec(disposition)
      const filename = (match?.[2] ?? match?.[3] ?? `schemascope.${format}`).trim()

      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = filename
      document.body.appendChild(a)
      a.click()
      a.remove()
      URL.revokeObjectURL(url)
    },
  },
}
