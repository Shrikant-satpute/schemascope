import { useCallback, useEffect, useRef, useState } from 'react'
import { Editor } from '@monaco-editor/react'
import { CircleCheck, Copy, Download, Eye, FileCode, FolderOpen, Play } from 'lucide-react'
import { api } from '../api'
import type {
  ApplyPlan,
  CompareResult,
  ObjectScriptDependencies,
  ObjectScriptOutcome,
  ObjectScriptPlan,
  ObjectStatus,
  ScriptProgress,
} from '../types'
import { STATUS } from '../status'
import { THEME_DARK, THEME_LIGHT, defineThemes } from '../monacoThemes'
import { canOpenFolder, openFolder } from '../host'
import { Button, Modal, Spinner } from './ui'
import { applyPlanOf } from '../scriptSections'
import { Banner, RunProgress } from './ScriptDialog'

const noop = () => undefined

const headingClass = 'text-[11px] font-semibold uppercase tracking-wide'

/** What each plan writes, in a sentence. */
const PLAN_TEXT: Record<ObjectScriptPlan, (target: string, typeLabel: string) => string> = {
  create: (t, type) =>
    type === 'Table' ? `Creates it in ${t}, with its checks, foreign keys and indexes.` : `Creates it in ${t}.`,
  alter: (t) => `Replaces ${t}'s version with the source's.`,
  tableChanges: (t) =>
    `Changes the table in ${t} in place: new and changed columns, keys and checks (and whether they are enabled and trusted), and indexes.`,
  tableType: (t) => `Drops and recreates the table type in ${t}, with the code that uses it.`,
  trigger: (t) => `Creates the missing trigger in ${t}.`,
  formatting: (t) => `Only the layout or comments differ. Re-scripts it so ${t}'s text matches the source.`,
  extra: (t) => `This only exists in ${t}, so there is nothing to script for it.`,
  identical: (t) => `Already the same in ${t}. There is nothing to script.`,
  unavailable: (t) => `This object cannot be read in ${t}, so it cannot be scripted.`,
}

type Tab = 'script' | 'rollback' | 'review'

/**
 * "Generate script" for one object against one target.
 *
 * It runs the same generator as "Create scripts", narrowed to this object and
 * whichever dependencies are ticked, and shows the text. Generating only
 * writes it to disk; while Read & write is on, "Apply to <target>" in the
 * footer hands the plan to ApplyDialog.
 */
export function ObjectScriptDialog({
  runId,
  result,
  objectKey,
  targetIndex,
  theme,
  writeMode,
  onApply,
  onClose,
  onToast,
}: {
  runId: string
  result: CompareResult
  /** `CompareRow.key`. */
  objectKey: string
  /** Index into `result.targets`. */
  targetIndex: number
  theme: 'dark' | 'light'
  /** Read & write is on for this session. */
  writeMode: boolean
  /** Opens the apply dialog for the plan the run came back with. */
  onApply: (plan: ApplyPlan) => void
  onClose: () => void
  onToast: (message: string) => void
}) {
  const target = result.targets[targetIndex]
  const row = result.rows.find((r) => r.key === objectKey)
  const status: ObjectStatus | undefined = row?.cells[targetIndex]?.status
  const objectName = row ? `${row.schema}.${row.name}` : objectKey
  const targetLabel = target?.label ?? 'the target'

  const [deps, setDeps] = useState<ObjectScriptDependencies | null>(null)
  const [depsError, setDepsError] = useState<string | null>(null)
  /** The keys of the dependencies to script with it; ticked to start with only where CREATE fails without them. */
  const [ticked, setTicked] = useState<Set<string>>(() => new Set())

  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<ScriptProgress | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<ObjectScriptOutcome | null>(null)
  const [tab, setTab] = useState<Tab>('script')
  const abortRef = useRef<AbortController | null>(null)
  const stoppedRef = useRef(false)

  useEffect(() => {
    let live = true
    void api.compare
      .objectScriptDependencies(runId, objectKey, targetIndex)
      .then((d) => {
        if (!live) return
        setDeps(d)
        setTicked(new Set(d.dependencies.filter((x) => !x.required && x.defaultIncluded).map((x) => x.key)))
      })
      .catch((e) => {
        if (live) setDepsError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      live = false
    }
  }, [runId, objectKey, targetIndex])

  // Closing the dialog mid-run stops the work on the server as well.
  useEffect(() => () => abortRef.current?.abort(), [])

  const close = useCallback(() => {
    abortRef.current?.abort()
    onClose()
  }, [onClose])

  const stop = useCallback(() => {
    stoppedRef.current = true
    abortRef.current?.abort()
  }, [])

  const plan = deps?.plan
  const required = deps?.dependencies.filter((d) => d.required) ?? []
  const optional = deps?.dependencies.filter((d) => !d.required) ?? []
  const unticked = optional.filter((d) => !ticked.has(d.key))
  // CREATE fails without these; the missing others only fail it at run time (a differing one's reason says why to wait)
  const blocking = unticked.filter((d) => d.defaultIncluded)
  const runtimeOnly = unticked.filter((d) => !d.defaultIncluded && d.status === 'missingInTarget')
  const names = (list: typeof optional) => list.map((d) => d.displayName).join(', ')
  const toggle = (key: string, on: boolean) =>
    setTicked((old) => {
      const next = new Set(old)
      if (on) next.add(key)
      else next.delete(key)
      return next
    })
  const canScript = !!deps && plan !== 'identical' && plan !== 'unavailable' && plan !== 'extra'

  async function generate() {
    if (running || !deps) return

    const controller = new AbortController()
    abortRef.current = controller
    stoppedRef.current = false
    setRunning(true)
    setProgress(null)
    setError(null)

    const progressId = newProgressId()
    const poll = window.setInterval(() => {
      void api.scripts
        .progress(progressId)
        .then((p) => {
          if (!controller.signal.aborted) setProgress((old) => (old && old.percent > p.percent ? old : p))
        })
        .catch(() => undefined)
    }, 500)

    try {
      const res = await api.compare.objectScript(
        runId,
        {
          targetIndex,
          key: objectKey,
          includeDependencies: [...required, ...optional.filter((d) => ticked.has(d.key))].map((d) => d.key),
          progressId,
        },
        controller.signal,
      )
      // An extra writes nothing; its DROP preview is the answer either way.
      if (res.success || res.dropPreview) {
        setOutcome(res)
        setTab('script')
      } else {
        setError(res.message || 'The script could not be generated.')
      }
    } catch (e) {
      if (controller.signal.aborted) {
        if (stoppedRef.current) setError('Stopped. Nothing was run, and nothing half-written is shown.')
        return
      }
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      window.clearInterval(poll)
      if (abortRef.current === controller) abortRef.current = null
      setRunning(false)
    }
  }

  const title = row
    ? `${row.typeLabel} ${objectName} → ${targetLabel}${target ? ` (${target.database})` : ''}`
    : `${objectName} → ${targetLabel}`

  const copy = async (text: string, what: string) => {
    try {
      await navigator.clipboard.writeText(text)
      onToast(`${what} copied`)
    } catch {
      onToast('Could not copy - select the text and copy it by hand.')
    }
  }

  // ---------------- extras: nothing to script, a DROP to look at ----------------

  if (plan === 'extra' || (!deps && status === 'onlyInTarget')) {
    return (
      <Modal title={title} onClose={running ? noop : close} width={760} footer={<Button onClick={close}>Close</Button>}>
        <div className="flex flex-col gap-3">
          <Header status={status} text={PLAN_TEXT.extra(targetLabel, row?.typeLabel ?? '')} />
          {depsError && <Banner tone="error">{depsError}</Banner>}
          {error && <Banner tone="error">{error}</Banner>}
          {outcome?.dropPreview ? (
            <>
              <Banner tone="error">
                Generate script does not run this. Create scripts can include it in 11_destructive, which is applied
                only with Read & write on. Dropping can break apps and lose data - check who uses it first.
              </Banner>
              <pre
                className="max-h-72 overflow-auto rounded-md border px-3 py-2 font-mono text-[12px]"
                style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
              >
                {outcome.dropPreview}
              </pre>
              <div>
                <Button variant="outline" size="sm" onClick={() => void copy(outcome.dropPreview!, 'DROP statement')}>
                  <Copy size={13} /> Copy
                </Button>
              </div>
            </>
          ) : outcome ? (
            <Banner tone="warning">{outcome.message || 'No DROP statement was returned for this object.'}</Banner>
          ) : (
            <div>
              <Button variant="outline" onClick={() => void generate()} disabled={running || !deps}>
                {running ? <Spinner size={13} /> : <Eye size={14} />} Show DROP statement
              </Button>
            </div>
          )}
        </div>
      </Modal>
    )
  }

  // ---------------- the result ----------------

  if (outcome) {
    const shown = tab === 'rollback' ? (outcome.rollback ?? '') : outcome.script
    const reviewCount = outcome.heldBack.length + outcome.warnings.length + (outcome.validation.passed ? 0 : 1)
    const canOpen = canOpenFolder(outcome.path)
    const applyPlan = writeMode ? applyPlanOf(runId, targetIndex, outcome) : null

    return (
      <Modal
        title={title}
        onClose={close}
        width={1040}
        footer={
          <>
            {applyPlan && (
              <Button variant="outline" onClick={() => onApply(applyPlan)}>
                <Play size={14} /> Apply to {applyPlan.applyTarget.label}…
              </Button>
            )}
            <div className="flex-1" />
            {canOpen && (
              <Button variant="outline" onClick={() => openFolder(outcome.path)}>
                <FolderOpen size={14} /> Open folder
              </Button>
            )}
            {tab !== 'review' && (
              <>
                <Button variant="outline" onClick={() => void copy(shown, tab === 'rollback' ? 'Rollback' : 'Script')} disabled={!shown}>
                  <Copy size={14} /> Copy
                </Button>
                <Button
                  variant="outline"
                  onClick={() => saveSql(shown, fileName(targetLabel, objectName, tab === 'rollback'))}
                  disabled={!shown}
                >
                  <Download size={14} /> Save .sql
                </Button>
              </>
            )}
            <Button variant="primary" onClick={close}>
              Close
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <Header status={status} text={plan ? PLAN_TEXT[plan](targetLabel, row?.typeLabel ?? '') : ''} />
          <PlanBanner plan={plan} target={targetLabel} />

          {!outcome.script.trim() && (
            <Banner tone="warning">
              Nothing was scripted.
              {outcome.heldBack.length > 0 ? ' See Review for what was held back and why.' : ''}
            </Banner>
          )}

          <div className="flex gap-0.5 self-start rounded-md p-0.5" style={{ background: 'var(--page)' }}>
            <TabButton active={tab === 'script'} onClick={() => setTab('script')} label="Script" />
            {outcome.rollback && (
              <TabButton active={tab === 'rollback'} onClick={() => setTab('rollback')} label="Rollback" />
            )}
            <TabButton
              active={tab === 'review'}
              onClick={() => setTab('review')}
              label={reviewCount > 0 ? `Review (${reviewCount})` : 'Review'}
            />
          </div>

          {tab === 'review' ? (
            <Review outcome={outcome} />
          ) : (
            <div className="overflow-hidden rounded-md border" style={{ borderColor: 'var(--line)' }}>
              <Editor
                height="440px"
                language="sql"
                value={shown}
                theme={theme === 'dark' ? THEME_DARK : THEME_LIGHT}
                beforeMount={defineThemes}
                options={{
                  readOnly: true,
                  domReadOnly: true,
                  lineNumbers: 'on',
                  minimap: { enabled: false },
                  fontSize: 12,
                  fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, monospace',
                  scrollBeyondLastLine: false,
                  automaticLayout: true,
                  contextmenu: false,
                  scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
                }}
              />
            </div>
          )}
          {tab === 'rollback' && (
            <div className="text-[11.5px]" style={{ color: 'var(--text-3)' }}>
              Puts back {targetLabel}'s current definition, as read during the compare.
            </div>
          )}
        </div>
      </Modal>
    )
  }

  // ---------------- the pre-check ----------------

  return (
    <Modal
      title={title}
      onClose={running ? noop : close}
      width={760}
      footer={
        <>
          {running ? <Button onClick={stop}>Stop</Button> : <Button onClick={close}>Cancel</Button>}
          <Button variant="primary" onClick={() => void generate()} disabled={!canScript || running}>
            {running ? <Spinner size={13} /> : <FileCode size={14} />}
            {running ? `Generating… ${progress?.percent ?? 0}%` : 'Generate'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Header status={status} text={plan ? PLAN_TEXT[plan](targetLabel, row?.typeLabel ?? '') : ''} />
        {error && <Banner tone="error">{error}</Banner>}
        {depsError && <Banner tone="error">{depsError}</Banner>}

        {!deps && !depsError && (
          <div className="flex items-center gap-2 text-[12px]" style={{ color: 'var(--text-2)' }}>
            <Spinner size={13} /> Checking what it needs in {targetLabel}...
          </div>
        )}

        {deps && (
          <>
            <PlanBanner plan={plan} target={targetLabel} />
            {deps.warnings.map((w, i) => (
              <Banner key={i} tone="warning">
                {w}
              </Banner>
            ))}

            {canScript && (
              <section>
                <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
                  Needs from {targetLabel}
                </h3>
                {deps.dependencies.length === 0 ? (
                  <div className="flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--st-same)' }}>
                    <CircleCheck size={13} /> Nothing else - everything it uses is already there.
                  </div>
                ) : (
                  <>
                    <ul className="flex flex-col gap-1">
                      {deps.dependencies.map((d) => (
                        <li key={d.key} className="flex items-start gap-2 text-[12px]">
                          {d.required ? (
                            <span className="w-[13px] shrink-0" />
                          ) : (
                            <input
                              type="checkbox"
                              className="mt-0.5 shrink-0"
                              checked={ticked.has(d.key)}
                              disabled={running}
                              onChange={(e) => toggle(d.key, e.target.checked)}
                              aria-label={`Also script ${d.displayName}`}
                            />
                          )}
                          <StatusChip status={d.status} />
                          <span className="min-w-0">
                            <span className="font-mono">{d.displayName}</span>
                            <span style={{ color: 'var(--text-3)' }}> {d.type}</span>
                            {d.required && (
                              <span className="ml-1.5 text-[11px]" style={{ color: 'var(--accent)' }}>
                                always included
                              </span>
                            )}
                            {d.reason && (
                              <span className="block text-[11px] leading-snug" style={{ color: 'var(--text-3)' }}>
                                {d.reason}
                              </span>
                            )}
                          </span>
                        </li>
                      ))}
                    </ul>

                    {optional.length > 0 && (
                      <div className="mt-1.5 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
                        Ticked items are scripted with it.
                      </div>
                    )}

                    {blocking.length > 0 && (
                      <div className="mt-1.5">
                        <Banner tone="error">
                          Without {names(blocking)} the CREATE fails on {targetLabel}.
                        </Banner>
                      </div>
                    )}
                    {runtimeOnly.length > 0 && (
                      <div className="mt-1.5">
                        <Banner tone="warning">
                          It will be created, but fails when it runs until {names(runtimeOnly)} exist in {targetLabel}.
                          SQL Server only checks those names at run time.
                        </Banner>
                      </div>
                    )}
                  </>
                )}
              </section>
            )}
          </>
        )}

        {running && <RunProgress progress={progress} />}
      </div>
    </Modal>
  )
}

/** Ids the server accepts: 8-64 letters, digits or dashes. */
function newProgressId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

/** "<target>_<schema.name>_<yyyyMMdd-HHmm>.sql", with anything Windows refuses in a name swapped out. */
function fileName(target: string, object: string, rollback: boolean): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}`
  const safe = (s: string) => s.replace(/[\\/:*?"<>|\s]+/g, '_')
  return `${safe(target)}_${safe(object)}_${stamp}${rollback ? '_rollback' : ''}.sql`
}

/** A browser download: the desktop window has no save dialog of its own to ask. */
function saveSql(text: string, name: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/sql' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

function StatusChip({ status }: { status?: ObjectStatus }) {
  if (!status) return null
  const s = STATUS[status]
  return (
    <span
      className="inline-flex shrink-0 items-center gap-1 rounded px-1.5 py-px text-[11px] font-medium"
      style={{ background: `color-mix(in srgb, ${s.colour} 14%, transparent)`, color: s.colour }}
      title={s.description}
    >
      <span className="font-mono">{s.symbol}</span> {s.label}
    </span>
  )
}

function Header({ status, text }: { status?: ObjectStatus; text: string }) {
  return (
    <div className="flex items-start gap-2 text-[12.5px]" style={{ color: 'var(--text-2)' }}>
      <StatusChip status={status} />
      <span>{text}</span>
    </div>
  )
}

/** The plans that need a second look before anyone runs them. */
function PlanBanner({ plan, target }: { plan?: ObjectScriptPlan; target: string }) {
  if (plan === 'tableType') {
    return (
      <Banner tone="error">
        A table type cannot be altered, so this drops it and every procedure or function that uses it, then creates them
        again. Anything calling them in {target} fails while it runs, and the deployed application's table-valued
        parameters fail until the application is updated - do it with the new release, in a quiet window.
      </Banner>
    )
  }
  if (plan === 'trigger') {
    return (
      <Banner tone="warning">
        The trigger fires on every matching insert, update or delete as soon as it exists. Check what it does to the
        processes already writing to that table in {target}.
      </Banner>
    )
  }
  if (plan === 'alter') {
    return (
      <Banner tone="warning">
        This overwrites {target}'s version. If that change was made on purpose it is lost - the Rollback tab has{' '}
        {target}'s current definition.
      </Banner>
    )
  }
  return null
}

function TabButton({ active, onClick, label }: { active: boolean; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded px-2.5 py-0.5 text-[12px] font-medium transition-colors"
      style={{
        background: active ? 'var(--surface)' : 'transparent',
        color: active ? 'var(--text)' : 'var(--text-3)',
      }}
    >
      {label}
    </button>
  )
}

/** Why something was not scripted, what is worth knowing, and the offline check. */
function Review({ outcome }: { outcome: ObjectScriptOutcome }) {
  const v = outcome.validation
  return (
    <div className="flex max-h-[440px] flex-col gap-4 overflow-auto">
      <Banner tone={v.passed ? 'success' : 'error'}>{v.summary || (v.passed ? 'Checked offline.' : 'The offline check found problems.')}</Banner>

      <section>
        <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
          Not scripted automatically
        </h3>
        {outcome.heldBack.length === 0 ? (
          <div className="flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--st-same)' }}>
            <CircleCheck size={13} /> Nothing was held back.
          </div>
        ) : (
          <ul className="flex flex-col gap-0.5 text-[12px]">
            {outcome.heldBack.map((h, i) => (
              <li key={i}>
                <span className="font-mono">{h.objectName}</span>
                <span style={{ color: 'var(--text-3)' }}> - {h.reason}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      {outcome.warnings.length > 0 && (
        <section>
          <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
            Worth knowing
          </h3>
          <div className="flex flex-col gap-1.5">
            {outcome.warnings.map((w, i) => (
              <Banner key={i} tone="warning">
                {w}
              </Banner>
            ))}
          </div>
        </section>
      )}

      {outcome.parts.length > 0 && (
        <section>
          <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
            In run order
          </h3>
          <ol className="flex flex-col gap-0.5 text-[12px]">
            {outcome.parts.map((p, i) => (
              <li key={i} className="flex gap-2">
                <span className="w-36 shrink-0 font-mono text-[11.5px]" style={{ color: 'var(--text-3)' }}>
                  {p.folder}
                </span>
                <span className="min-w-0 truncate font-mono" title={p.file}>
                  {p.objectName}
                </span>
                <span style={{ color: 'var(--text-3)' }}>{p.kind}</span>
              </li>
            ))}
          </ol>
          {outcome.dependenciesIncluded.length > 0 && (
            <div className="mt-1 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
              Includes {outcome.dependenciesIncluded.length} dependenc
              {outcome.dependenciesIncluded.length === 1 ? 'y' : 'ies'}: {outcome.dependenciesIncluded.join(', ')}
            </div>
          )}
        </section>
      )}

      {outcome.path && (
        <div className="text-[11.5px]" style={{ color: 'var(--text-3)' }}>
          Written to <span className="font-mono">{outcome.path}</span> in {(outcome.elapsedMs / 1000).toFixed(1)} s.
        </div>
      )}
    </div>
  )
}
