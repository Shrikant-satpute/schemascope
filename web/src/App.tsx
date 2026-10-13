import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, Database, History, Lock, Moon, Server, Share2, Sun, Zap } from 'lucide-react'
import { api } from './api'
import type {
  CompareHistoryEntry,
  CompareOptions,
  CompareResult,
  ConnectionSettings,
  ProgressEvent,
} from './types'
import { ConnectionsView } from './components/ConnectionsView'
import { HistoryView } from './components/HistoryView'
import { ShareView } from './components/ShareView'
import { SetupBar, type TargetSlot } from './components/SetupBar'
import { ResultsWorkspace } from './components/ResultsWorkspace'
import { OptionsDialog, countNonDefault } from './components/OptionsDialog'
import { ScriptDialog } from './components/ScriptDialog'
import { Button, EmptyState, Spinner } from './components/ui'

type View = 'compare' | 'connections' | 'history' | 'share'

const THEME_KEY = 'schemascope.theme'

/** Light unless this machine has asked for dark before. */
function initialTheme(): 'dark' | 'light' {
  try {
    return localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light'
  } catch {
    return 'light'
  }
}

export default function App() {
  const [view, setView] = useState<View>('compare')
  const [theme, setTheme] = useState<'dark' | 'light'>(initialTheme)
  const [connections, setConnections] = useState<ConnectionSettings[]>([])
  const [history, setHistory] = useState<CompareHistoryEntry[]>([])
  const [secretsProtected, setSecretsProtected] = useState(true)

  const [source, setSource] = useState<TargetSlot | null>(null)
  const [targets, setTargets] = useState<TargetSlot[]>([])
  const [options, setOptions] = useState<CompareOptions | null>(null)
  const [showOptions, setShowOptions] = useState(false)

  const [runId, setRunId] = useState<string | null>(null)
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<ProgressEvent[]>([])
  const [result, setResult] = useState<CompareResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [hint, setHint] = useState<string | null>(null)

  const [exporting, setExporting] = useState(false)
  const [publishing, setPublishing] = useState(false)
  /** Index into `result.targets` whose "Create scripts" dialog is open, or null. */
  const [scriptTarget, setScriptTarget] = useState<number | null>(null)
  const closeScripts = useCallback(() => setScriptTarget(null), [])
  const [toast, setToast] = useState<string | null>(null)

  const say = useCallback((message: string) => {
    setToast(message)
    setTimeout(() => setToast(null), 4000)
  }, [])

  // ---------------- bootstrap ----------------

  const loadConnections = useCallback(async () => {
    const list = await api.connections.list()
    setConnections(list)

    // A connection can be deleted from the picker or the Connections screen.
    // Anything pointing at one that is gone has to let go of it, or the setup
    // bar would sit there showing "unknown".
    const alive = new Set(list.map((c) => c.id))
    setSource((s) => (s && !alive.has(s.connectionId) ? null : s))
    setTargets((t) => t.filter((x) => alive.has(x.connectionId)))
  }, [])

  const loadHistory = useCallback(async () => {
    setHistory(await api.history.list())
  }, [])

  useEffect(() => {
    void (async () => {
      try {
        const [health, list, defaults, past] = await Promise.all([
          api.health(),
          api.connections.list(),
          api.defaultOptions(),
          api.history.list(),
        ])
        setSecretsProtected(health.secretsProtected)
        setConnections(list)
        setOptions(defaults)
        setHistory(past)
        if (list.length === 0) setView('connections')
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e))
      }
    })()
  }, [])

  /**
   * Put a past comparison back on the compare screen. The old result goes with
   * it: it belongs to a different pairing, and leaving it up would have the
   * setup bar saying one thing while the grid below shows another.
   */
  const useHistoryEntry = useCallback(
    (entry: CompareHistoryEntry) => {
      const alive = new Set(connections.map((c) => c.id))
      const toSlot = (d: CompareHistoryEntry['source']): TargetSlot | null =>
        d.connectionId && alive.has(d.connectionId)
          ? { connectionId: d.connectionId, database: d.database }
          : null

      const nextSource = toSlot(entry.source)
      if (!nextSource) {
        say('That source connection has been deleted, so this one cannot be loaded.')
        return
      }

      const nextTargets = entry.targets.map(toSlot).filter((t): t is TargetSlot => t !== null)
      const dropped = entry.targets.length - nextTargets.length

      setSource(nextSource)
      setTargets(nextTargets)
      setOptions(entry.options)
      setResult(null)
      setError(null)
      setHint(null)
      setView('compare')

      say(
        dropped > 0
          ? `Loaded. ${dropped} deleted connection${dropped === 1 ? '' : 's'} left out - press Compare when ready.`
          : 'Loaded - press Compare when you are ready.',
      )
    },
    [connections, say],
  )

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme)
    try {
      localStorage.setItem(THEME_KEY, theme)
    } catch {
      /* private mode - the choice just does not survive a restart */
    }
  }, [theme])

  // ---------------- run a compare ----------------

  async function runCompare() {
    if (!source || targets.length === 0 || !options) return

    setRunning(true)
    setError(null)
    setHint(null)
    setResult(null)
    setProgress([])

    try {
      const { runId: id } = await api.compare.start({
        source: { connectionId: source.connectionId, database: source.database },
        targets: targets.map((t) => ({ connectionId: t.connectionId, database: t.database })),
        options,
      })
      setRunId(id)

      const stop = api.compare.events(id, (e) => {
        setProgress((p) => [...p.filter((x) => x.sourceId !== e.sourceId || e.sourceId === '*'), e])

        if (e.type === 'done' || e.type === 'error') {
          stop()
          void (async () => {
            try {
              const summary = await api.compare.get(id)
              if (summary.status === 'done' && summary.result) {
                setResult(summary.result)
                // The run has just filed itself in history - pick it up so the
                // tab is right without having to be opened twice.
                void loadHistory().catch(() => undefined)
              } else {
                setError(summary.error ?? 'The compare did not finish.')
                setHint(summary.hint ?? null)
              }
            } catch (err) {
              setError(err instanceof Error ? err.message : String(err))
            } finally {
              setRunning(false)
            }
          })()
        }
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setRunning(false)
    }
  }

  async function cancelCompare() {
    if (runId) await api.compare.cancel(runId).catch(() => undefined)
    setRunning(false)
  }

  // ---------------- export ----------------

  async function doExport(format: 'html' | 'csv' | 'md' | 'sql', differencesOnly: boolean) {
    if (!runId) return
    setExporting(true)
    try {
      if (format === 'sql') {
        const folder = window.prompt('Folder to write the .sql files into:', 'D:\\SchemaScope-export')
        if (!folder) return
        const res = await api.compare.exportTo(runId, 'sql', { outputFolder: folder, differencesOnly })
        say(res && 'message' in res ? (res.message ?? 'Done') : 'Done')
      } else {
        await api.compare.exportTo(runId, format, { differencesOnly })
        say('Report downloaded')
      }
    } catch (e) {
      say(e instanceof Error ? e.message : String(e))
    } finally {
      setExporting(false)
    }
  }

  // ---------------- share ----------------

  /**
   * Publishes the run on screen and copies the link. The whole comparison is
   * written to disk first, so the link keeps working after this run has fallen
   * out of memory.
   */
  async function doShare() {
    if (!runId) return
    setPublishing(true)
    try {
      const { share, started, error: shareError } = await api.shares.publish(runId)

      if (!started) {
        say(shareError ?? 'The comparison was published, but the listener would not start.')
      } else if (share.urls[0]) {
        // urls[0] is the real network card - the one most likely to work for
        // somebody else. See ShareNetwork.Addresses.
        await navigator.clipboard.writeText(share.urls[0].url).catch(() => undefined)
        say('Link copied. Open the Share tab to see who is reading it.')
      } else {
        say('Shared. Open the Share tab for the link.')
      }

      setView('share')
    } catch (e) {
      say(e instanceof Error ? e.message : String(e))
    } finally {
      setPublishing(false)
    }
  }

  // ---------------- render ----------------

  const optionBadge = options ? countNonDefault(options) : 0

  return (
    <div className="flex h-full flex-col">
      <header
        className="flex shrink-0 items-center gap-3 border-b px-4 py-2"
        style={{ borderColor: 'var(--line)', background: 'var(--surface)' }}
      >
        <div className="flex items-center gap-2">
          <Zap size={17} style={{ color: 'var(--accent)' }} />
          <span className="text-[14px] font-semibold tracking-tight">SchemaScope</span>
        </div>

        <nav className="ml-3 flex gap-0.5 rounded-md p-0.5" style={{ background: 'var(--page)' }}>
          <NavTab active={view === 'compare'} onClick={() => setView('compare')} icon={<Database size={13} />} label="Compare" />
          <NavTab
            active={view === 'connections'}
            onClick={() => setView('connections')}
            icon={<Server size={13} />}
            label="Connections"
            badge={connections.length || undefined}
          />
          <NavTab
            active={view === 'history'}
            onClick={() => {
              setView('history')
              void loadHistory().catch(() => undefined)
            }}
            icon={<History size={13} />}
            label="History"
            badge={history.length || undefined}
          />
          <NavTab
            active={view === 'share'}
            onClick={() => setView('share')}
            icon={<Share2 size={13} />}
            label="Share"
          />
        </nav>

        <div className="flex-1" />

        <span
          className="inline-flex items-center gap-1.5 rounded px-2 py-1 text-[11.5px] font-medium"
          style={{ background: 'color-mix(in srgb, var(--st-same) 14%, transparent)', color: 'var(--st-same)' }}
          title="SchemaScope can only read. There is no code in it that writes to a database."
        >
          <Lock size={12} /> Read only
        </span>

        <Button size="sm" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label="Toggle theme">
          {theme === 'dark' ? <Sun size={14} /> : <Moon size={14} />}
        </Button>
      </header>

      {view === 'connections' ? (
        <ConnectionsView
          connections={connections}
          onChanged={loadConnections}
          secretsProtected={secretsProtected}
        />
      ) : view === 'history' ? (
        <HistoryView
          history={history}
          connections={connections}
          onUse={useHistoryEntry}
          onChanged={() => void loadHistory().catch(() => undefined)}
        />
      ) : view === 'share' ? (
        <ShareView hasResult={!!result} onGoToCompare={() => setView('compare')} onToast={say} />
      ) : (
        <div className="flex min-h-0 flex-1 flex-col">
          <SetupBar
            connections={connections}
            source={source}
            targets={targets}
            running={running}
            elapsedMs={result?.totalMs ?? null}
            onSourceChange={setSource}
            onTargetsChange={setTargets}
            onRun={runCompare}
            onCancel={cancelCompare}
            onOpenOptions={() => setShowOptions(true)}
            optionCount={optionBadge}
            onConnectionsChanged={loadConnections}
          />

          {running && <ProgressStrip events={progress} />}

          {error && (
            <div
              className="flex items-start gap-2 px-4 py-2.5 text-[12.5px]"
              style={{ background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)', color: 'var(--st-miss)' }}
            >
              <AlertTriangle size={15} className="mt-px shrink-0" />
              <div>
                <div>{error}</div>
                {hint && (
                  <div className="mt-1" style={{ color: 'var(--text-2)' }}>
                    {hint}
                  </div>
                )}
              </div>
            </div>
          )}

          {!result && !running && (
            <div className="flex-1">
              <EmptyState
                icon={<Database size={30} />}
                title={connections.length === 0 ? 'Add a connection to get started' : 'Ready when you are'}
                body={
                  connections.length === 0
                    ? 'SchemaScope compares one source database against as many targets as you like, all at the same time.'
                    : 'Pick a source, add one or more targets, then press Compare. A thousand objects takes about a second.'
                }
                action={
                  connections.length === 0 ? (
                    <Button variant="primary" onClick={() => setView('connections')}>
                      <Server size={14} /> Add a connection
                    </Button>
                  ) : undefined
                }
              />
            </div>
          )}

          {result && runId && (
            <ResultsWorkspace
              key={runId}
              result={result}
              theme={theme}
              loadObject={(key) => api.compare.object(runId, key)}
              findReferences={(name) => api.compare.references(runId, name)}
              findUses={(key) => api.compare.uses(runId, key)}
              onExport={doExport}
              exporting={exporting}
              onShare={doShare}
              sharing={publishing}
              onCreateScripts={setScriptTarget}
            />
          )}
        </div>
      )}

      {showOptions && options && (
        <OptionsDialog
          options={options}
          onClose={() => setShowOptions(false)}
          onApply={(o) => {
            setOptions(o)
            setShowOptions(false)
          }}
        />
      )}

      {/* Mounted here because App owns the runId; keyed so a different target
          always starts from a fresh form. */}
      {scriptTarget !== null && result && runId && result.targets[scriptTarget] && (
        <ScriptDialog
          key={`${runId}:${scriptTarget}`}
          runId={runId}
          result={result}
          targetIndex={scriptTarget}
          onClose={closeScripts}
          onToast={say}
        />
      )}

      {toast && (
        <div
          className="ss-fade fixed bottom-4 left-1/2 z-50 -translate-x-1/2 rounded-md border px-4 py-2 text-[12.5px] shadow-xl"
          style={{ background: 'var(--surface)', borderColor: 'var(--line)' }}
        >
          {toast}
        </div>
      )}
    </div>
  )
}

function NavTab({
  active,
  onClick,
  icon,
  label,
  badge,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
  badge?: number
}) {
  return (
    <button
      onClick={onClick}
      className="inline-flex items-center gap-1.5 rounded px-2.5 py-1 text-[12.5px] font-medium transition-colors"
      style={{
        background: active ? 'var(--surface)' : 'transparent',
        color: active ? 'var(--text)' : 'var(--text-3)',
      }}
    >
      {icon}
      {label}
      {badge !== undefined && (
        <span className="tabular-nums" style={{ color: 'var(--text-3)' }}>
          {badge}
        </span>
      )}
    </button>
  )
}

function ProgressStrip({ events }: { events: ProgressEvent[] }) {
  const latest = events[events.length - 1]
  const perSource = events.filter((e) => e.sourceId !== '*')

  return (
    <div className="border-b px-4 py-2" style={{ borderColor: 'var(--line)', background: 'var(--surface-2)' }}>
      <div className="flex items-center gap-2 text-[12.5px]">
        <Spinner />
        <span>{latest?.stage ?? 'Starting'}</span>
        {latest?.detail && <span style={{ color: 'var(--text-3)' }}>{latest.detail}</span>}
        <div className="flex-1" />
        {latest && (
          <span className="font-mono text-[11.5px]" style={{ color: 'var(--text-3)' }}>
            {latest.elapsedMs} ms
          </span>
        )}
      </div>

      {perSource.length > 0 && (
        <div className="mt-1.5 flex flex-wrap gap-3">
          {perSource.map((e) => (
            <span key={e.sourceId} className="flex items-center gap-1.5 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
              <span className="inline-block h-1 w-16 overflow-hidden rounded-full" style={{ background: 'var(--line)' }}>
                <span
                  className="block h-full rounded-full transition-all"
                  style={{ width: `${e.percent}%`, background: 'var(--accent)' }}
                />
              </span>
              {e.label || e.sourceId} - {e.stage}
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
