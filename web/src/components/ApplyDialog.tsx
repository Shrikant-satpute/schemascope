import { useCallback, useRef, useState } from 'react'
import { CircleCheck, CircleX, Play, ShieldAlert } from 'lucide-react'
import { api } from '../api'
import { engineOf, useEngines } from '../engines'
import type { ApplyPlan, ApplyResult, CompareResult, ScriptProgress } from '../types'
import { Button, Input, Modal, Spinner } from './ui'
import { Banner } from './ScriptDialog'

const noop = () => undefined

const headingClass = 'text-[11px] font-semibold uppercase tracking-wide'

const DESTRUCTIVE = '11_destructive'

/**
 * "Apply to <target>" for a plan that "Create scripts" or "Generate script"
 * wrote while Read & write was on.
 *
 * The server re-checks everything asked here - write mode, the plan, the typed
 * database name, the protected flag, the data-loss tick - so this dialog is
 * the explanation, not the guard. It is mounted by App rather than by the
 * script dialogs, so it stays open while the compare runs again afterwards.
 */
export function ApplyDialog({
  plan,
  writeMode,
  compareRunning,
  compareResult,
  compareError,
  onRecompare,
  onClose,
}: {
  plan: ApplyPlan
  /** Read & write is on right now. Turning it off mid-way leaves nothing to apply with. */
  writeMode: boolean
  /** App's compare state, to show the match after the automatic re-compare. */
  compareRunning: boolean
  compareResult: CompareResult | null
  compareError: string | null
  /** Runs the compare again for the pair on screen. False when there is nothing to run. */
  onRecompare: () => boolean
  onClose: () => void
}) {
  const t = plan.applyTarget
  // the engine's own promise and typed-name rule; SQL Server keeps the wording built in below
  const applyText = engineOf(useEngines(), t.engine).apply
  const [sections, setSections] = useState<Set<string>>(
    () => new Set(plan.planSections.filter((s) => s.defaultSelected && s.files > 0).map((s) => s.folder)),
  )
  const [acceptDataLoss, setAcceptDataLoss] = useState(false)
  const [typed, setTyped] = useState('')

  const [running, setRunning] = useState(false)
  const [stopping, setStopping] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  const [progress, setProgress] = useState<ScriptProgress | null>(null)
  /** Every step the server reported, in order: the live per-file log. */
  const [log, setLog] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<ApplyResult | null>(null)
  /** Set once the re-compare has been asked for, so the old result is never read as the new one. */
  const [recompared, setRecompared] = useState(false)
  const progressIdRef = useRef<string | null>(null)

  const toggle = (folder: string, on: boolean) => {
    setSections((s) => {
      const next = new Set(s)
      if (on) next.add(folder)
      else next.delete(folder)
      return next
    })
    if (folder === DESTRUCTIVE && !on) setAcceptDataLoss(false)
  }

  const close = useCallback(() => onClose(), [onClose])

  const stop = useCallback(() => {
    const id = progressIdRef.current
    if (!id) return
    setStopping(true)
    void api.apply.stop(id).catch(() => stopFailed())
  }, [])

  // For a file that does not finish (waiting on locks, say): cancel it now - it is rolled back.
  const cancelNow = useCallback(() => {
    const id = progressIdRef.current
    if (!id) return
    setCancelling(true)
    void api.apply.stop(id, true).catch(() => stopFailed())
  }, [])

  // the server did not take the stop (the apply had already finished, or it was not reached): never pretend it did
  function stopFailed() {
    setStopping(false)
    setCancelling(false)
    setError('The stop did not reach the apply - it may already have finished. Its result shows here when it does.')
  }

  const chosen = plan.planSections.filter((s) => sections.has(s.folder))
  const fileCount = chosen.reduce((n, s) => n + s.files, 0)
  const destructiveChosen = sections.has(DESTRUCTIVE)
  // an empty name confirms nothing: the server refuses it too
  const nameMatches = t.database.trim().length > 0 && typed === t.database
  const canApply =
    writeMode && !t.protected && !running && chosen.length > 0 && nameMatches && (!destructiveChosen || acceptDataLoss)

  async function apply() {
    if (!canApply) return

    const progressId = newProgressId()
    progressIdRef.current = progressId
    setRunning(true)
    setStopping(false)
    setCancelling(false)
    setProgress(null)
    setLog([])
    setError(null)

    const poll = window.setInterval(() => {
      void api.scripts
        .progress(progressId)
        .then((p) => {
          setProgress((old) => (old && old.percent > p.percent ? old : p))
          setLog((old) => (p.step && old[old.length - 1] !== p.step ? [...old, p.step] : old))
        })
        .catch(() => undefined)
    }, 500)

    try {
      const res = await api.compare.apply(plan.runId, {
        planId: plan.planId,
        targetIndex: plan.targetIndex,
        // the plan's own (run) order, whatever order they were ticked in
        sections: chosen.map((s) => s.folder),
        confirmDatabase: typed,
        acceptDataLoss: destructiveChosen && acceptDataLoss,
        progressId,
      })
      setOutcome(res)
      // Something may have changed in the target: compare again so the grid is not left showing the old state.
      if (res.applied.some((f) => f.ok)) setRecompared(onRecompare())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      window.clearInterval(poll)
      progressIdRef.current = null
      setRunning(false)
    }
  }

  const title = `Apply to ${t.label}`

  // ---------------- the result ----------------

  if (outcome) {
    const ok = outcome.applied.filter((f) => f.ok).length
    const failed = outcome.applied.find((f) => !f.ok)
    // every file of the sections that were run, not only those that got as far as running
    const total = Math.max(fileCount, outcome.applied.length)
    const after = recompared ? matchFor(compareResult, plan) : null

    return (
      <Modal
        title={title}
        subtitle={`${serverText(t)} / ${t.database}`}
        onClose={close}
        width={760}
        footer={
          <Button variant="primary" onClick={close}>
            Close
          </Button>
        }
      >
        <div className="flex flex-col gap-3">
          <Banner tone={outcome.success ? 'success' : 'error'}>
            <div>{outcome.message}</div>
            <div className="mt-0.5">
              {ok.toLocaleString()} of {total.toLocaleString()} file
              {total === 1 ? '' : 's'} applied.
              {!outcome.success &&
                ok > 0 &&
                (failed
                  ? ' The files before the one that stopped stay applied - only that file was rolled back.'
                  : ' They stay applied; the files after them were not run.')}
            </div>
            {outcome.stoppedAt && (
              <div className="mt-0.5">
                Stopped at <span className="font-mono">{outcome.stoppedAt}</span>.
              </div>
            )}
          </Banner>

          {failed?.error && (
            <pre
              className="max-h-40 overflow-auto whitespace-pre-wrap rounded-md border px-3 py-2 font-mono text-[11.5px]"
              style={{ background: 'var(--page)', borderColor: 'var(--line)', color: 'var(--st-miss)' }}
            >
              {failed.error}
            </pre>
          )}

          <FileLog files={outcome.applied} />

          <section className="text-[12px]" role="status" aria-live="polite">
            {recompared ? (
              compareRunning ? (
                <span className="flex items-center gap-2" style={{ color: 'var(--text-2)' }}>
                  <Spinner size={13} /> Comparing again…
                </span>
              ) : compareError ? (
                <Banner tone="error">The compare after the apply did not finish: {compareError}</Banner>
              ) : after ? (
                <span style={{ color: 'var(--text-2)' }}>
                  Compared again: {t.label} now matches the source{' '}
                  <span className="font-semibold tabular-nums" style={{ color: 'var(--text)' }}>
                    {after.matchPercent}%
                  </span>{' '}
                  ({after.totalDifferences.toLocaleString()} difference{after.totalDifferences === 1 ? '' : 's'} left).
                </span>
              ) : (
                <span style={{ color: 'var(--text-3)' }}>Compared again - see the results behind this dialog.</span>
              )
            ) : ok > 0 ? (
              <span style={{ color: 'var(--text-3)' }}>Press Compare to see where {t.label} stands now.</span>
            ) : null}
          </section>

          <div className="text-[11px]" style={{ color: 'var(--text-3)' }}>
            Recorded in the apply log in SchemaScope's local settings database as{' '}
            <span className="font-mono">{outcome.auditId}</span>.
          </div>
        </div>
      </Modal>
    )
  }

  // ---------------- the form, and the run ----------------

  return (
    <Modal
      title={title}
      subtitle="Runs the scripts against this database. Read every line of this first."
      onClose={running ? noop : close}
      width={760}
      footer={
        t.protected ? (
          <Button variant="primary" onClick={close}>
            Close
          </Button>
        ) : (
          <>
            {running ? (
              stopping ? (
                <Button onClick={cancelNow} disabled={cancelling} title="Cancels the file that is running; it is rolled back">
                  {cancelling ? 'Cancelling this file…' : 'Cancel this file now (roll it back)'}
                </Button>
              ) : (
                <Button onClick={stop}>Stop after this file</Button>
              )
            ) : (
              <Button onClick={close}>Cancel</Button>
            )}
            <Button
              variant="primary"
              onClick={() => void apply()}
              disabled={!canApply}
              style={{ background: 'var(--st-miss)' }}
            >
              {running ? <Spinner size={13} /> : <Play size={14} />}
              {running
                ? `Applying… ${progress?.percent ?? 0}%`
                : `Apply ${fileCount.toLocaleString()} file${fileCount === 1 ? '' : 's'} to ${t.database}`}
            </Button>
          </>
        )
      }
    >
      <div className="flex flex-col gap-4">
        <TargetCard plan={plan} />

        {t.protected ? (
          <Banner tone="error">
            <span className="font-semibold">Protected server — scripts only.</span>{' '}
            {t.protectedItself === false
              ? 'Another saved connection on the same host is marked "Protected — never apply scripts here", and that protects every database and port on the host, so SchemaScope will not run anything on this one either.'
              : 'This connection is marked "Protected — never apply scripts here", so SchemaScope will not run anything on it.'}{' '}
            Run the scripts from disk through your own release process instead.
          </Banner>
        ) : (
          <>
            {!writeMode && <Banner tone="error">Write mode is off, so nothing can be applied. Turn it on to apply.</Banner>}
            {error && <Banner tone="error">{error}</Banner>}

            <section>
              <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
                What to apply, in run order
              </h3>
              <div className="flex flex-col">
                {plan.planSections.map((s) => {
                  const on = sections.has(s.folder)
                  const colour =
                    s.risk === 'destructive' ? 'var(--st-miss)' : s.risk === 'changesCode' ? 'var(--st-diff)' : undefined
                  return (
                    <label
                      key={s.folder}
                      className="flex cursor-pointer items-start gap-2.5 rounded-md px-2 py-1.5 transition-colors hover:bg-[var(--raised)]"
                      style={
                        s.risk === 'destructive'
                          ? { background: 'color-mix(in srgb, var(--st-miss) 8%, transparent)' }
                          : undefined
                      }
                    >
                      <input
                        type="checkbox"
                        className="mt-[3px]"
                        checked={on}
                        disabled={running || s.files === 0}
                        onChange={(e) => toggle(s.folder, e.target.checked)}
                      />
                      <span className="min-w-0 flex-1">
                        <span className="flex items-baseline gap-2">
                          <span className="font-mono text-[11.5px]" style={{ color: colour ?? 'var(--text-3)' }}>
                            {s.folder}
                          </span>
                          <span className="text-[12.5px]" style={colour ? { color: colour } : undefined}>
                            {s.title}
                          </span>
                          <span className="ml-auto text-[11.5px] tabular-nums" style={{ color: 'var(--text-3)' }}>
                            {s.files.toLocaleString()} file{s.files === 1 ? '' : 's'}
                          </span>
                        </span>
                        {s.warning && (
                          <span
                            className="mt-0.5 block text-[11px] leading-snug"
                            style={{ color: colour ?? 'var(--st-diff)' }}
                          >
                            {s.warning}
                          </span>
                        )}
                      </span>
                    </label>
                  )
                })}
              </div>

              {destructiveChosen && (
                <label
                  className="mt-1.5 flex cursor-pointer items-start gap-2.5 rounded-md px-2 py-1.5 text-[12.5px] font-medium"
                  style={{ color: 'var(--st-miss)' }}
                >
                  <input
                    type="checkbox"
                    className="mt-[3px]"
                    checked={acceptDataLoss}
                    disabled={running}
                    onChange={(e) => setAcceptDataLoss(e.target.checked)}
                  />
                  I understand this can lose data
                </label>
              )}

              {plan.heldBack > 0 && (
                <div className="mt-1.5 px-2 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
                  {plan.heldBack.toLocaleString()} item{plan.heldBack === 1 ? ' was' : 's were'} held back for review
                  and {plan.heldBack === 1 ? 'is' : 'are'} never applied - see 99_review.
                </div>
              )}
            </section>

            <Banner tone="warning">
              {applyText ? (
                applyText.transactions
              ) : (
                <>
                  Each file runs in its own transaction. If one fails, that file is rolled back and the run stops there -
                  the files before it stay applied. The compare runs again afterwards.
                </>
              )}
            </Banner>

            <label className="flex flex-col gap-1">
              <span className="text-[12px]" style={{ color: 'var(--text-2)' }}>
                Type the database name <span className="font-mono font-semibold">{t.database}</span> to confirm
                {applyText?.typedNameExact && ' (letter case counts)'}
              </span>
              <Input
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
                disabled={running}
                spellCheck={false}
                autoComplete="off"
                aria-label={`Type ${t.database} to confirm`}
                className="font-mono"
                style={typed && !nameMatches ? { borderColor: 'var(--st-miss)' } : undefined}
              />
            </label>

            {running && <ApplyProgress progress={progress} log={log} />}
            {running && stopping && (
              <div className="text-[12px]" style={{ color: 'var(--st-diff)' }} role="status">
                {cancelling
                  ? 'Cancelling the file that is running - it will be rolled back.'
                  : 'Stopping after this file. If it does not finish (it may be waiting on locks), cancel it now.'}
              </div>
            )}
          </>
        )}
      </div>
    </Modal>
  )
}

/** Where it will run, as big as it can be: the thing to read twice. */
function TargetCard({ plan }: { plan: ApplyPlan }) {
  const t = plan.applyTarget
  return (
    <div
      className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 rounded-md border px-3 py-2.5 text-[12.5px]"
      style={{
        borderColor: t.protected ? 'var(--st-miss)' : 'var(--st-diff)',
        background: 'var(--page)',
      }}
    >
      <span style={{ color: 'var(--text-3)' }}>Target</span>
      <span className="flex items-center gap-1.5 font-semibold">
        {t.protected && <ShieldAlert size={13} style={{ color: 'var(--st-miss)' }} />}
        {t.label}
      </span>
      <span style={{ color: 'var(--text-3)' }}>Server</span>
      <span className="font-mono">{serverText(t)}</span>
      <span style={{ color: 'var(--text-3)' }}>Database</span>
      <span className="font-mono text-[14px] font-semibold">{t.database}</span>
      <span style={{ color: 'var(--text-3)' }}>Runs as</span>
      <span className="font-mono">{t.login}</span>
    </div>
  )
}

function ApplyProgress({ progress, log }: { progress: ScriptProgress | null; log: string[] }) {
  const percent = progress?.percent ?? 0
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-2)' }}>
        <Spinner size={13} />
        <span className="min-w-0 flex-1 truncate">{progress?.step ?? 'Starting'}</span>
        <span className="font-mono text-[12px] font-semibold tabular-nums" style={{ color: 'var(--text)' }}>
          {percent}%
        </span>
      </div>
      <div
        className="h-1.5 w-full overflow-hidden rounded-full"
        style={{ background: 'var(--line)' }}
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        aria-label="Apply progress"
      >
        <div
          className="h-full rounded-full transition-[width] duration-500 ease-out"
          style={{ width: `${percent}%`, background: 'var(--st-diff)' }}
        />
      </div>
      {log.length > 0 && (
        <ol
          className="max-h-40 overflow-auto rounded-md border px-2.5 py-1.5 font-mono text-[11.5px]"
          style={{ background: 'var(--page)', borderColor: 'var(--line)', color: 'var(--text-2)' }}
          role="log"
          aria-live="polite"
          aria-label="Apply log"
        >
          {log.map((line, i) => (
            <li key={i}>{line}</li>
          ))}
        </ol>
      )}
    </div>
  )
}

/** One line per file the run reached: ok or the error, and how long it took. */
function FileLog({ files }: { files: ApplyResult['applied'] }) {
  if (files.length === 0) return null
  return (
    <section>
      <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
        Files
      </h3>
      <ol
        className="flex max-h-72 flex-col gap-0.5 overflow-auto text-[12px]"
        role="log"
        aria-label="Applied files"
      >
        {files.map((f, i) => (
          <li key={i} className="flex items-start gap-2">
            {f.ok ? (
              <CircleCheck size={13} className="mt-px shrink-0" style={{ color: 'var(--st-same)' }} aria-label="ok" />
            ) : (
              <CircleX size={13} className="mt-px shrink-0" style={{ color: 'var(--st-miss)' }} aria-label="failed" />
            )}
            <span className="w-32 shrink-0 truncate font-mono text-[11.5px]" style={{ color: 'var(--text-3)' }}>
              {f.folder}
            </span>
            <span className="min-w-0 flex-1">
              <span className="font-mono" title={f.file}>
                {f.objectName || f.file}
              </span>
              {f.error && (
                <span className="block text-[11px] leading-snug" style={{ color: 'var(--st-miss)' }}>
                  {f.error}
                </span>
              )}
            </span>
            <span className="shrink-0 text-[11px] tabular-nums" style={{ color: 'var(--text-3)' }}>
              {f.elapsedMs.toLocaleString()} ms
            </span>
          </li>
        ))}
      </ol>
    </section>
  )
}

/** The server as the user wrote it, with the port for engines that keep it apart (PostgreSQL). */
function serverText(t: ApplyPlan['applyTarget']): string {
  return t.port ? `${t.server}:${t.port}` : t.server
}

/**
 * The target in a fresh result that is the database just applied to. The compare runs again with the same targets
 * in the same order, so it is the plan's own target index - checked to be the same engine, label and database (letter
 * case counting outside SQL Server), so a result from another comparison is never read as this one's.
 */
function matchFor(result: CompareResult | null, plan: ApplyPlan) {
  if (!result) return null
  const t = plan.applyTarget
  const engine = t.engine ?? 'sqlServer'
  const x = result.targets[plan.targetIndex]
  if (!x || x.failed || (x.engine ?? 'sqlServer') !== engine || x.label !== t.label) return null
  const sameDatabase =
    engine === 'sqlServer' ? x.database.localeCompare(t.database, undefined, { sensitivity: 'accent' }) === 0 : x.database === t.database
  if (!sameDatabase) return null
  if (t.port != null && x.port != null && x.port !== t.port) return null
  return x
}

/** Ids the server accepts: 8-64 letters, digits or dashes. */
function newProgressId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}
