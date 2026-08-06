import { useState } from 'react'
import { ArrowRight, Clock, History, Play, Trash2, AlertTriangle } from 'lucide-react'
import type { CompareHistoryDatabase, CompareHistoryEntry, ConnectionSettings } from '../types'
import { api } from '../api'
import { dbColour, dbKey } from '../dbColour'
import { Button, EmptyState, Panel } from './ui'
import { countNonDefault } from './OptionsDialog'

/**
 * Every compare that has finished, drawn the way it was set up: source on the
 * left, everything it was compared against on the right.
 *
 * Setting the same comparison up again by hand is the most repetitive thing in
 * the app, so one click puts it back in the setup bar. It stops there rather
 * than running - reading it back and pressing Compare yourself is one step, and
 * a compare that starts on its own is a compare you did not ask for.
 */
export function HistoryView({
  history,
  connections,
  onUse,
  onChanged,
}: {
  history: CompareHistoryEntry[]
  connections: ConnectionSettings[]
  onUse: (entry: CompareHistoryEntry) => void
  onChanged: () => void
}) {
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const [confirmClear, setConfirmClear] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const alive = new Set(connections.map((c) => c.id))
  const isAvailable = (d: CompareHistoryDatabase) => !!d.connectionId && alive.has(d.connectionId)

  async function run(work: () => Promise<void>) {
    setError(null)
    try {
      await work()
      setConfirmDelete(null)
      setConfirmClear(false)
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="flex h-full flex-col overflow-y-auto px-6 py-5">
      <div className="mx-auto w-full max-w-4xl">
        <div className="mb-5 flex items-end justify-between gap-4">
          <div>
            <h1 className="text-[19px] font-semibold">History</h1>
            <p className="mt-0.5 text-[12.5px]" style={{ color: 'var(--text-2)' }}>
              Every comparison you have run. Pick one and it goes straight back into the setup bar -
              source, targets and the options that run used - ready for you to press Compare.
            </p>
          </div>

          {history.length > 0 &&
            (confirmClear ? (
              <div className="flex shrink-0 items-center gap-1.5">
                <span className="text-[12px]" style={{ color: 'var(--text-2)' }}>
                  Forget all {history.length}?
                </span>
                <Button variant="danger" onClick={() => run(() => api.history.clear())}>
                  Clear all
                </Button>
                <Button onClick={() => setConfirmClear(false)}>Keep</Button>
              </div>
            ) : (
              <Button variant="outline" onClick={() => setConfirmClear(true)}>
                <Trash2 size={14} /> Clear history
              </Button>
            ))}
        </div>

        {error && (
          <div
            className="mb-4 flex items-start gap-2 rounded-md px-3 py-2 text-[12.5px]"
            style={{
              background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
              color: 'var(--st-miss)',
            }}
          >
            <AlertTriangle size={15} className="mt-px shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {history.length === 0 ? (
          <Panel className="h-72">
            <EmptyState
              icon={<History size={30} />}
              title="Nothing here yet"
              body="Run a comparison and it lands here. After that you never have to pick the same databases twice - one click loads the whole setup back."
            />
          </Panel>
        ) : (
          <div className="grid gap-2.5">
            {history.map((entry) => {
              const sourceGone = !isAvailable(entry.source)
              const missingTargets = entry.targets.filter((t) => !isAvailable(t)).length
              const optionCount = countNonDefault(entry.options)

              return (
                <Panel key={entry.id} className="px-4 py-3">
                  <div className="flex items-start gap-4">
                    <button
                      onClick={() => !sourceGone && onUse(entry)}
                      disabled={sourceGone}
                      className="min-w-0 flex-1 text-left disabled:cursor-not-allowed"
                      title={sourceGone ? undefined : 'Load this back into the setup bar'}
                    >
                      {/* the picture: what was compared against what */}
                      <div className="flex flex-wrap items-start gap-x-3 gap-y-2">
                        <Side label="Source">
                          <DbChip db={entry.source} available={!sourceGone} />
                        </Side>

                        <ArrowRight size={16} className="mt-[18px]" style={{ color: 'var(--text-3)' }} />

                        <Side label={`Compare against (${entry.targets.length})`}>
                          <div className="flex flex-wrap gap-1.5">
                            {entry.targets.map((t, i) => (
                              <DbChip key={`${t.connectionId}-${t.database}-${i}`} db={t} available={isAvailable(t)} />
                            ))}
                          </div>
                        </Side>
                      </div>

                      <div
                        className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[11.5px]"
                        style={{ color: 'var(--text-3)' }}
                      >
                        <span className="inline-flex items-center gap-1">
                          <Clock size={11} />
                          {timeAgo(entry.lastRunUtc)}
                        </span>
                        {entry.runCount > 1 && <span>run {entry.runCount} times</span>}
                        <span className="tabular-nums">{entry.objectCount.toLocaleString()} objects</span>
                        <span className="tabular-nums">
                          {entry.differenceCount.toLocaleString()} with differences
                        </span>
                        <span className="tabular-nums">
                          {entry.lowestMatchPercent.toFixed(1)}% lowest match
                        </span>
                        <span className="tabular-nums">{entry.totalMs} ms</span>
                        {optionCount > 0 && (
                          <span style={{ color: 'var(--accent)' }}>
                            {optionCount} non-default option{optionCount === 1 ? '' : 's'}
                          </span>
                        )}
                      </div>

                      {(sourceGone || missingTargets > 0) && (
                        <div
                          className="mt-1.5 inline-flex items-start gap-1.5 text-[11.5px]"
                          style={{ color: 'var(--st-diff)' }}
                        >
                          <AlertTriangle size={12} className="mt-px shrink-0" />
                          {sourceGone
                            ? 'The source connection has been deleted, so this one cannot be loaded.'
                            : `${missingTargets} of these connections ${
                                missingTargets === 1 ? 'has' : 'have'
                              } been deleted and will be left out.`}
                        </div>
                      )}
                    </button>

                    <div className="flex shrink-0 items-center gap-1">
                      <Button variant="primary" onClick={() => onUse(entry)} disabled={sourceGone}>
                        <Play size={14} /> Use this
                      </Button>

                      {confirmDelete === entry.id ? (
                        <>
                          <Button variant="danger" onClick={() => run(() => api.history.remove(entry.id))}>
                            Delete
                          </Button>
                          <Button onClick={() => setConfirmDelete(null)}>Keep</Button>
                        </>
                      ) : (
                        <Button
                          variant="danger"
                          onClick={() => setConfirmDelete(entry.id)}
                          aria-label="Forget this comparison"
                          title="Forget this comparison"
                        >
                          <Trash2 size={14} />
                        </Button>
                      )}
                    </div>
                  </div>
                </Panel>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}

function Side({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <div
        className="mb-1 text-[10.5px] font-medium uppercase tracking-wide"
        style={{ color: 'var(--text-3)' }}
      >
        {label}
      </div>
      {children}
    </div>
  )
}

/** One database in the picture, wearing the same colour it wears everywhere else. */
function DbChip({ db, available }: { db: CompareHistoryDatabase; available: boolean }) {
  const colour = dbColour(dbKey(db.server, db.database))

  return (
    <span
      className="inline-flex min-w-0 flex-col rounded-md border px-2.5 py-1"
      style={{
        borderColor: available ? colour.line : 'var(--line)',
        background: available ? colour.tint : 'transparent',
        borderStyle: available ? 'solid' : 'dashed',
        opacity: available ? 1 : 0.6,
      }}
      title={`${db.server} / ${db.database}${available ? '' : ' - connection deleted'}`}
    >
      <span className="truncate text-[12.5px] font-medium">{db.label || db.server}</span>
      <span className="truncate font-mono text-[10.5px]" style={{ color: 'var(--text-3)' }}>
        {db.server} / {db.database}
      </span>
    </span>
  )
}

function timeAgo(iso: string): string {
  const then = Date.parse(iso)
  if (Number.isNaN(then)) return ''

  const seconds = Math.max(0, (Date.now() - then) / 1000)
  if (seconds < 60) return 'just now'

  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`

  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`

  const days = Math.round(hours / 24)
  if (days < 7) return `${days} day${days === 1 ? '' : 's'} ago`

  return new Date(then).toLocaleString()
}
