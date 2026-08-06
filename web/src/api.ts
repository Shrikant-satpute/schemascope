import type {
  CompareHistoryEntry,
  CompareProfile,
  CompareRequest,
  ConnectionSettings,
  ConnectionTestResult,
  DatabaseListItem,
  ExportResult,
  Health,
  ObjectDetail,
  ProgressEvent,
  RunSummary,
  CompareOptions,
} from './types'

const base = ''

/**
 * The desktop shell hands the API token over in the launch URL. It is lifted
 * into memory once and wiped from the address bar, so it never ends up in a
 * copied link or in anything the page later navigates to.
 *
 * In development there is no token here: the Vite proxy adds the header to
 * everything it forwards instead.
 */
const token = (() => {
  try {
    const found = new URLSearchParams(window.location.search).get('k')
    if (found) window.history.replaceState({}, '', window.location.pathname)
    return found
  } catch {
    return null
  }
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

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const body = await res.json()
      if (body?.message) message = body.message
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

  profiles: {
    list: () => req<CompareProfile[]>('/api/profiles'),
    save: (p: CompareProfile) =>
      req<CompareProfile>('/api/profiles', { method: 'POST', body: JSON.stringify(p) }),
    remove: (id: string) => req<void>(`/api/profiles/${id}`, { method: 'DELETE' }),
  },

  defaultOptions: () => req<CompareOptions>('/api/options/defaults'),

  compare: {
    start: (request: CompareRequest) =>
      req<{ runId: string }>('/api/compare', { method: 'POST', body: JSON.stringify(request) }),
    get: (runId: string) => req<RunSummary>(`/api/compare/${runId}`),
    cancel: (runId: string) => req<void>(`/api/compare/${runId}/cancel`, { method: 'POST' }),
    object: (runId: string, key: string) =>
      req<ObjectDetail>(`/api/compare/${runId}/object?key=${encodeURIComponent(key)}`),

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
