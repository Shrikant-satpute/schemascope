import { useCallback, useEffect, useRef, useState } from 'react'
import {
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  CircleCheck,
  Copy,
  FileCode,
  FolderOpen,
  FolderSearch,
  Play,
} from 'lucide-react'
import { api } from '../api'
import type { ApplyPlan, CompareResult, FolderResult, ScriptOutcome, ScriptProgress, ScriptSection } from '../types'
import { canOpenFolder, hasNativeHost, openFolder, pickFolder, type PickFolderOptions } from '../host'
import {
  DEFAULT_SCRIPT_OPTIONS,
  DEFAULT_SCRIPT_SECTIONS,
  applyPlanOf,
  looksLikeAbsoluteWindowsPath,
  parsePatterns,
  scriptSectionsFor,
  sectionDependencyWarnings,
} from '../scriptSections'
import { engineOf, supports, useEngines } from '../engines'
import { Button, Field, Input, Modal, Spinner, Toggle } from './ui'

/** How many held-back objects the summary lists before it points at 99_review. */
const HELD_BACK_SHOWN = 6

/** The largest "Large table (rows)" the server accepts (a 32-bit int). */
const MAX_ROWS = 2_147_483_647

/** The server adds this warning when validation fails; the summary's red banner already says it. */
const VALIDATION_WARNING_PREFIX = 'Validation found problems'

const noop = () => undefined

const headingClass = 'text-[11px] font-semibold uppercase tracking-wide'

/**
 * "Create scripts" for one target of a finished compare.
 *
 * The dialog only collects choices and shows what came back. The server reads
 * both databases, writes the numbered folders, and checks every file offline -
 * generating runs nothing against a database. Only while Read & write is on
 * does the outcome carry a plan, which "Apply to <target>" hands to ApplyDialog.
 *
 * It has two faces in the same Modal: the form, and after a successful run a
 * summary of what was written. A run that fails keeps the form, with the
 * server's message in the error banner, so the user can change a choice and
 * try again without starting over.
 */
export function ScriptDialog({
  runId,
  result,
  targetIndex,
  writeMode,
  onApply,
  onClose,
  onToast,
}: {
  runId: string
  result: CompareResult
  /** Index into `result.targets`, as the tile passed it. */
  targetIndex: number
  /** Read & write is on for this session. */
  writeMode: boolean
  /** Opens the apply dialog for the plan the run came back with. */
  onApply: (plan: ApplyPlan) => void
  onClose: () => void
  onToast: (message: string) => void
}) {
  const target = result.targets[targetIndex]
  // the sections, hints and runner come from the server for this engine (SQL Server: the built-in list)
  const engines = useEngines()
  const engineText = engineOf(engines, target?.engine).scripts ?? null
  const sectionList = scriptSectionsFor(engineText)
  const canDestroy = supports(engines, target?.engine, 'destructive')

  const [sections, setSections] = useState<Set<ScriptSection>>(() => new Set(DEFAULT_SCRIPT_SECTIONS))
  const [outputFolder, setOutputFolder] = useState('')
  const [quarantineFolder, setQuarantineFolder] = useState('')
  const [excludeText, setExcludeText] = useState('')
  const [checkExistingData, setCheckExistingData] = useState(DEFAULT_SCRIPT_OPTIONS.checkExistingData)
  const [largeTableRows, setLargeTableRows] = useState(String(DEFAULT_SCRIPT_OPTIONS.largeTableRows))
  const [showAdvanced, setShowAdvanced] = useState(false)
  /** Only offered while Read & write is on, and only sent while it still is. */
  const [includeDestructive, setIncludeDestructive] = useState(false)

  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<ScriptProgress | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<ScriptOutcome | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  // set by Stop, so the aborted request reads as "stopped" rather than as an error or a silent close
  const stoppedRef = useRef(false)

  // Prefill with the folder the last run used. Only when the user has not
  // typed anything yet - a slow answer must not overwrite their own choice.
  useEffect(() => {
    let live = true
    void api.scripts
      .lastFolder()
      .then((r) => {
        if (live && r?.path) setOutputFolder((f) => f || r.path || '')
      })
      .catch(() => undefined)
    return () => {
      live = false
    }
  }, [])

  // Closing the dialog mid-run stops the work on the server as well.
  useEffect(() => () => abortRef.current?.abort(), [])

  // Stable, so Modal's Escape listener is not re-attached on every keystroke.
  const close = useCallback(() => {
    abortRef.current?.abort()
    onClose()
  }, [onClose])

  // Stop keeps the dialog open and says what is left on disk. While a run is going, Escape, a click
  // on the backdrop and X do nothing: a stray one would stop the run with nobody told about it.
  const stop = useCallback(() => {
    stoppedRef.current = true
    abortRef.current?.abort()
  }, [])

  const toggle = (key: ScriptSection, on: boolean) =>
    setSections((s) => {
      const next = new Set(s)
      if (on) next.add(key)
      else next.delete(key)
      return next
    })

  const browse = async (current: string, set: (path: string) => void, options: PickFolderOptions) => {
    const picked = await pickFolder(current.trim() || undefined, options)
    if (picked) set(picked)
  }

  const folderTrimmed = outputFolder.trim()
  const folderInvalid = folderTrimmed.length > 0 && !looksLikeAbsoluteWindowsPath(folderTrimmed)
  const quarantineTrimmed = quarantineFolder.trim()
  const quarantineInvalid = quarantineTrimmed.length > 0 && !looksLikeAbsoluteWindowsPath(quarantineTrimmed)
  const rows = Number(largeTableRows)
  const rowsInvalid = !Number.isInteger(rows) || rows < 1 || rows > MAX_ROWS
  // folder order, whatever order they were ticked in, and never a section this engine does not have
  const chosen = sectionList.filter((s) => s.applicable !== false && sections.has(s.key)).map((s) => s.key)
  const dependencyWarnings = sectionDependencyWarnings(new Set(chosen))

  const canGenerate =
    !running && chosen.length > 0 && folderTrimmed.length > 0 && !folderInvalid && !quarantineInvalid && !rowsInvalid

  async function generate() {
    if (!canGenerate) return

    const controller = new AbortController()
    abortRef.current = controller
    stoppedRef.current = false
    setRunning(true)
    setProgress(null)
    setError(null)

    // The POST only answers at the end, so ask how far it is twice a second meanwhile.
    // A poll that fails (before the run has started, or after it ended) just keeps the last value.
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
      const res = await api.compare.scripts(
        runId,
        {
          targetIndex,
          progressId,
          options: {
            sections: chosen,
            outputFolder: folderTrimmed,
            quarantineFolder: quarantineTrimmed || undefined,
            excludePatterns: parsePatterns(excludeText),
            checkExistingData,
            largeTableRows: rows,
            probeTimeoutSeconds: DEFAULT_SCRIPT_OPTIONS.probeTimeoutSeconds,
            includeDestructive: writeMode && canDestroy && includeDestructive,
          },
        },
        controller.signal,
      )

      if (res.success) {
        setOutcome(res)
        onToast(`${res.fileCount.toLocaleString()} files written`)
        if (res.path && canOpenFolder(res.path)) openFolder(res.path)
      } else {
        const message = res.message ?? 'The scripts could not be written.'
        // the server names the partial folder itself; add it only if it did not
        setError(
          res.path && res.fileCount > 0 && !message.includes(res.path)
            ? `${message} What was written so far is in ${res.path} - it is not finished, do not run it.`
            : message,
        )
      }
    } catch (e) {
      if (controller.signal.aborted) {
        if (stoppedRef.current) {
          setError(
            `Stopped. A partial folder may have been left in ${folderTrimmed}, marked "INCOMPLETE - DO NOT RUN". ` +
              'It was not finished or checked - do not run anything from it.',
          )
        }
        return
      }
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      window.clearInterval(poll)
      if (abortRef.current === controller) abortRef.current = null
      setRunning(false)
    }
  }

  const subtitle = target ? `Make ${target.label} look like ${result.source.label}` : undefined

  if (outcome) {
    const plan = writeMode ? applyPlanOf(runId, targetIndex, outcome) : null
    return (
      <Modal
        title="Scripts written"
        subtitle={subtitle}
        onClose={close}
        width={720}
        footer={
          <>
            {plan && (
              <Button variant="outline" onClick={() => onApply(plan)}>
                <Play size={14} /> Apply to {plan.applyTarget.label}…
              </Button>
            )}
            <div className="flex-1" />
            <Button variant="primary" onClick={close}>
              Close
            </Button>
          </>
        }
      >
        <ScriptSummary outcome={outcome} runner={engineText?.runner ?? 'sqlcmd'} onToast={onToast} />
      </Modal>
    )
  }

  return (
    <Modal
      title="Create scripts"
      subtitle={subtitle}
      onClose={running ? noop : close}
      width={720}
      footer={
        <>
          {running ? <Button onClick={stop}>Stop</Button> : <Button onClick={close}>Cancel</Button>}
          <Button variant="primary" onClick={() => void generate()} disabled={!canGenerate}>
            {running ? <Spinner size={13} /> : <FileCode size={14} />}
            {running ? `Writing scripts… ${progress?.percent ?? 0}%` : 'Generate'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {error && <Banner tone="error">{error}</Banner>}

        <section>
          <div className="mb-1 flex items-center gap-2">
            <h3 className={headingClass} style={{ color: 'var(--text-3)' }}>
              What to script
            </h3>
            <div className="flex-1" />
            <span className="text-[11px]" style={{ color: 'var(--text-3)' }}>
              <button
                type="button"
                className="hover:underline disabled:opacity-50 disabled:no-underline"
                style={{ color: 'var(--accent)' }}
                disabled={running}
                onClick={() => setSections(new Set(sectionList.filter((s) => s.applicable !== false).map((s) => s.key)))}
              >
                all
              </button>
              {' / '}
              <button
                type="button"
                className="hover:underline disabled:opacity-50 disabled:no-underline"
                style={{ color: 'var(--accent)' }}
                disabled={running}
                onClick={() => setSections(new Set())}
              >
                none
              </button>
            </span>
          </div>

          <div className="flex flex-col">
            {sectionList.map((s) => {
              const applicable = s.applicable !== false
              const on = applicable && sections.has(s.key)
              return (
                <label
                  key={s.key}
                  className={`flex items-start gap-2.5 rounded-md px-2 py-1.5 transition-colors ${
                    applicable ? 'cursor-pointer hover:bg-[var(--raised)]' : 'opacity-55'
                  }`}
                >
                  <input
                    type="checkbox"
                    className="mt-[3px]"
                    checked={on}
                    disabled={running || !applicable}
                    onChange={(e) => toggle(s.key, e.target.checked)}
                  />
                  <span
                    className="mt-px w-10 shrink-0 font-mono text-[11.5px] tabular-nums"
                    style={{ color: 'var(--text-3)' }}
                  >
                    {s.folder}
                  </span>
                  <span className="min-w-0">
                    <span className="block text-[12.5px]">{s.label}</span>
                    <span className="block text-[11px] leading-snug" style={{ color: 'var(--text-3)' }}>
                      {s.hint}
                    </span>
                    {on && s.warning && (
                      <span
                        className="mt-0.5 flex items-start gap-1 text-[11px] leading-snug"
                        style={{ color: 'var(--st-diff)' }}
                      >
                        <AlertTriangle size={11} className="mt-px shrink-0" />
                        {s.warning}
                      </span>
                    )}
                  </span>
                </label>
              )
            })}
          </div>

          {chosen.length === 0 && (
            <div className="mt-1.5 px-2 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
              Tick at least one to generate.
            </div>
          )}

          {writeMode && canDestroy && (
            <DestructiveOption checked={includeDestructive} onChange={setIncludeDestructive} disabled={running} />
          )}
          {engineText && (
            <div className="mt-1.5 px-2 text-[11px] leading-snug" style={{ color: 'var(--text-3)' }}>
              Each folder's run_all.ps1 runs its files with {engineText.runner}, one transaction per file. {engineText.validation}
            </div>
          )}
        </section>

        {dependencyWarnings.length > 0 && (
          <div className="flex flex-col gap-1.5">
            {dependencyWarnings.map((w) => (
              <Banner key={w} tone="warning">
                {w}
              </Banner>
            ))}
          </div>
        )}

        <Field
          label="Output folder"
          hint={
            folderInvalid
              ? 'This needs a full path, like D:\\Scripts or \\\\server\\share\\Scripts.'
              : 'A new folder named after the target and the time is made inside it. Nothing already there is changed.'
          }
        >
          <div className="flex gap-2">
            <Input
              className="min-w-0 flex-1"
              value={outputFolder}
              placeholder="D:\SchemaScope-scripts"
              spellCheck={false}
              disabled={running}
              onChange={(e) => setOutputFolder(e.target.value)}
              style={folderInvalid ? { borderColor: 'var(--st-miss)' } : undefined}
            />
            <Button
              type="button"
              variant="outline"
              disabled={running}
              onClick={() =>
                void browse(outputFolder, setOutputFolder, {
                  title: 'Choose where to write the scripts',
                  suggestion: 'D:\\SchemaScope-scripts',
                })
              }
            >
              <FolderSearch size={14} /> Browse
            </Button>
          </div>
        </Field>

        <section className="border-t pt-3" style={{ borderColor: 'var(--line)' }}>
          <button
            type="button"
            className="inline-flex items-center gap-1 hover:underline"
            style={{ color: 'var(--text-3)' }}
            onClick={() => setShowAdvanced((v) => !v)}
            aria-expanded={showAdvanced}
          >
            {showAdvanced ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
            <span className={headingClass}>Advanced</span>
          </button>

          {showAdvanced && (
            <div className="mt-2 grid grid-cols-2 gap-x-6 gap-y-3">
              <Field
                className="col-span-2"
                label="Leave out what is in this folder"
                hint={
                  quarantineInvalid
                    ? 'This needs a full path, like D:\\Repo\\_Quarantine.'
                    : 'Optional. Any object named like a .sql file in this folder (or below it) is not created.'
                }
              >
                <div className="flex gap-2">
                  <Input
                    className="min-w-0 flex-1"
                    value={quarantineFolder}
                    placeholder="D:\Projects\Backend\Mes.Db.Schema\_Quarantine"
                    spellCheck={false}
                    disabled={running}
                    onChange={(e) => setQuarantineFolder(e.target.value)}
                    style={quarantineInvalid ? { borderColor: 'var(--st-miss)' } : undefined}
                  />
                  <Button
                    type="button"
                    variant="outline"
                    disabled={running}
                    onClick={() =>
                      void browse(quarantineFolder, setQuarantineFolder, {
                        title: 'Choose the folder of .sql files to leave out',
                      })
                    }
                  >
                    <FolderSearch size={14} /> Browse
                  </Button>
                </div>
              </Field>

              <Field
                className="col-span-2"
                label="Leave out these names"
                hint="Comma separated. * and ? work, for example dbo.tmp_*, *_old. Matches schema.name or just the name."
              >
                <Input
                  value={excludeText}
                  placeholder="dbo.tmp_*, *_backup"
                  spellCheck={false}
                  disabled={running}
                  onChange={(e) => setExcludeText(e.target.value)}
                />
              </Field>

              <div className="-mx-2">
                <Toggle
                  checked={checkExistingData}
                  onChange={running ? noop : setCheckExistingData}
                  label="Check the data already in the target"
                  hint="Counts rows that would stop a key, check or link from being added. Read only, but slower on big tables."
                />
              </div>

              <Field
                label="Large table (rows)"
                hint={
                  rowsInvalid
                    ? 'Enter a whole number from 1 to 2,147,483,647.'
                    : 'Changes to tables at least this big are flagged as a locking risk.'
                }
              >
                <Input
                  type="number"
                  min={1}
                  max={MAX_ROWS}
                  step={1000}
                  value={largeTableRows}
                  disabled={running}
                  onChange={(e) => setLargeTableRows(e.target.value)}
                  style={rowsInvalid ? { borderColor: 'var(--st-miss)' } : undefined}
                />
              </Field>
            </div>
          )}
        </section>

        {running && <RunProgress progress={progress} />}
      </div>
    </Modal>
  )
}

/**
 * "Include changes that can lose data": shown only while Read & write is on,
 * off to start with. The server refuses it when write mode is off anyway.
 */
export function DestructiveOption({
  checked,
  onChange,
  disabled,
}: {
  checked: boolean
  onChange: (on: boolean) => void
  disabled?: boolean
}) {
  return (
    <label
      className="mt-1.5 flex cursor-pointer items-start gap-2.5 rounded-md px-2 py-1.5 transition-colors hover:bg-[var(--raised)]"
      style={{ background: 'color-mix(in srgb, var(--st-miss) 8%, transparent)' }}
    >
      <input
        type="checkbox"
        className="mt-[3px]"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="min-w-0">
        <span className="block text-[12.5px]" style={{ color: 'var(--st-miss)' }}>
          Include changes that can lose data
        </span>
        <span className="block text-[11px] leading-snug" style={{ color: 'var(--st-miss)' }}>
          Also writes 11_destructive: changes that can throw data away. It is never applied unless you tick it again
          when applying and confirm that data can be lost.
        </span>
      </span>
    </label>
  )
}

/** Ids the server accepts: 8-64 letters, digits or dashes. */
function newProgressId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}

/** The bar under the options while a run is going: the step it is on and how far along the whole run is. */
export function RunProgress({ progress }: { progress: ScriptProgress | null }) {
  const percent = progress?.percent ?? 0
  return (
    <div className="flex flex-col gap-1.5" role="status" aria-live="polite">
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
        aria-label="Script generation progress"
      >
        <div
          className="h-full rounded-full transition-[width] duration-500 ease-out"
          style={{ width: `${percent}%`, background: 'var(--accent)' }}
        />
      </div>
      <div className="text-[11.5px]" style={{ color: 'var(--text-3)' }}>
        Only reads the two databases - nothing is run on them. On a big database this can take a minute.
      </div>
    </div>
  )
}

/**
 * What a successful run wrote. The detail (every held-back object, every
 * validation problem) is in 99_review on disk; this is the overview that says
 * whether it is worth opening.
 */
function ScriptSummary({
  outcome,
  runner,
  onToast,
}: {
  outcome: ScriptOutcome
  /** The client the runners call ("sqlcmd", "psql"). */
  runner: string
  onToast: (message: string) => void
}) {
  const path = outcome.path ?? ''
  const v = outcome.validation
  const canOpen = canOpenFolder(path)
  // the red validation banner already says this one
  const warnings = v.passed ? outcome.warnings : outcome.warnings.filter((w) => !w.startsWith(VALIDATION_WARNING_PREFIX))

  const copyPath = async () => {
    try {
      await navigator.clipboard.writeText(path)
      onToast('Path copied')
    } catch {
      onToast('Could not copy - select the path and copy it by hand.')
    }
  }

  return (
    <div className="flex flex-col gap-4">
      {path && (
        <section>
          <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
            Written to
          </h3>
          <div className="flex items-center gap-2">
            <code
              className="min-w-0 flex-1 truncate rounded-md border px-2.5 py-1.5 font-mono text-[12px]"
              style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
              title={path}
            >
              {path}
            </code>
            <Button variant="outline" size="sm" onClick={() => void copyPath()}>
              <Copy size={13} /> Copy
            </Button>
            {canOpen && (
              <Button variant="outline" size="sm" onClick={() => openFolder(path)}>
                <FolderOpen size={13} /> Open folder
              </Button>
            )}
          </div>
          {hasNativeHost() && !canOpen && (
            <div className="mt-1 text-[11px]" style={{ color: 'var(--text-3)' }}>
              This folder name has a comma, so it cannot be opened from here - use Copy.
            </div>
          )}
          <div className="mt-1 text-[11px]" style={{ color: 'var(--text-3)' }}>
            {outcome.fileCount.toLocaleString()} files in {(outcome.elapsedMs / 1000).toFixed(1)} s. Read README.md
            first, and run on a rehearsal copy before the real one.
          </div>
        </section>
      )}

      {v.level && !v.level.parsed ? (
        <Banner tone="warning">
          Lexical check only - not validated: {v.batches.toLocaleString()} statements were split as {runner} reads them and
          their quotes and brackets checked, but no parser read them. {v.level.notValidatedReason}
          {v.problems.length > 0 && (
            <ul className="mt-1 list-disc pl-4">
              {v.problems.slice(0, 3).map((p, i) => (
                <li key={i}>{p}</li>
              ))}
            </ul>
          )}
        </Banner>
      ) : v.level && v.passed ? (
        <Banner tone="success">
          Parsed offline with {v.level.parser ?? 'the engine\'s parser'}: {v.batches.toLocaleString()} statements parse, and{' '}
          {v.modulesExact.toLocaleString()} of {v.modulesChecked.toLocaleString()} code bodies are the source&apos;s text exactly.
          Code bodies are not compile-checked.
        </Banner>
      ) : (
      <Banner tone={v.passed ? 'success' : 'error'}>
        {v.passed ? (
          <>
            Checked offline: {v.batches.toLocaleString()} batches parse, and {v.modulesExact.toLocaleString()} of{' '}
            {v.modulesChecked.toLocaleString()} code bodies match the source exactly.
          </>
        ) : (
          <>
            The offline check found problems: {v.syntaxErrors.toLocaleString()} syntax error
            {v.syntaxErrors === 1 ? '' : 's'}, {v.modulesExact.toLocaleString()} of {v.modulesChecked.toLocaleString()}{' '}
            code bodies exact
            {v.problems.length > 0 ? `, ${v.problems.length} other problem${v.problems.length === 1 ? '' : 's'}` : ''}.
            See 99_review\validation.md before running anything.
            {v.problems.length > 0 && (
              <ul className="mt-1 list-disc pl-4">
                {v.problems.slice(0, 3).map((p, i) => (
                  <li key={i}>{p}</li>
                ))}
              </ul>
            )}
          </>
        )}
      </Banner>
      )}

      {outcome.folders.length > 0 && (
        <section>
          <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
            Folders
          </h3>
          <table className="w-full border-collapse text-[12px]">
            <thead>
              <tr className="text-left text-[11px]" style={{ color: 'var(--text-3)' }}>
                <th className="py-1 pr-3 font-medium">Folder</th>
                <th className="py-1 pr-3 text-right font-medium">Files</th>
                <th className="py-1 pr-3 font-medium">In RUN.ps1</th>
                <th className="py-1 font-medium">Undo scripts</th>
              </tr>
            </thead>
            <tbody>
              {outcome.folders.map((f) => (
                <tr key={f.folder} className="border-t align-top" style={{ borderColor: 'var(--line-soft)' }}>
                  <td className="py-1.5 pr-3">
                    <span className="font-mono text-[11.5px]">{f.folder}</span>
                    {f.title && (
                      <span className="ml-2" style={{ color: 'var(--text-2)' }}>
                        {f.title}
                      </span>
                    )}
                    {f.notes.map((n, i) => (
                      <div key={i} className="text-[11px] leading-snug" style={{ color: 'var(--text-3)' }}>
                        {n}
                      </div>
                    ))}
                  </td>
                  <td className="py-1.5 pr-3 text-right tabular-nums">{f.files.toLocaleString()}</td>
                  <td className="py-1.5 pr-3" style={{ color: f.inRunAll ? 'var(--text)' : 'var(--text-3)' }}>
                    {runAllText(f)}
                  </td>
                  <td className="py-1.5" style={{ color: f.hasRollback ? 'var(--text)' : 'var(--text-3)' }}>
                    {f.files > 0 && f.hasRollback ? 'rollback\\' : '-'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <section>
        <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
          Held back for review
        </h3>
        {outcome.heldBack.length === 0 ? (
          <div className="flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--st-same)' }}>
            <CircleCheck size={13} /> Nothing was held back.
          </div>
        ) : (
          <>
            <div className="text-[12px]" style={{ color: 'var(--text-2)' }}>
              {outcome.heldBack.length.toLocaleString()} item{outcome.heldBack.length === 1 ? ' was' : 's were'} not
              scripted because running {outcome.heldBack.length === 1 ? 'it' : 'them'} could fail or change behaviour.
              They are listed in 99_review.
            </div>
            <ul className="mt-1 flex flex-col gap-0.5 text-[11.5px]">
              {outcome.heldBack.slice(0, HELD_BACK_SHOWN).map((h, i) => (
                <li key={i} className="flex gap-2">
                  <span className="w-20 shrink-0 truncate font-mono" style={{ color: 'var(--text-3)' }} title={h.section}>
                    {h.section}
                  </span>
                  <span className="min-w-0">
                    <span className="font-mono">{h.object}</span>
                    <span style={{ color: 'var(--text-3)' }}> - {h.reason}</span>
                  </span>
                </li>
              ))}
            </ul>
            {outcome.heldBack.length > HELD_BACK_SHOWN && (
              <div className="mt-0.5 text-[11px]" style={{ color: 'var(--text-3)' }}>
                and {(outcome.heldBack.length - HELD_BACK_SHOWN).toLocaleString()} more.
              </div>
            )}
          </>
        )}
      </section>

      {warnings.length > 0 && (
        <section>
          <h3 className={`mb-1 ${headingClass}`} style={{ color: 'var(--text-3)' }}>
            Worth knowing
          </h3>
          <div className="flex flex-col gap-1.5">
            {warnings.map((w, i) => (
              <Banner key={i} tone="warning">
                {w}
              </Banner>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}

/** The "In RUN.ps1" cell: nothing for a folder that was not written, and 02's opt-in switch. */
function runAllText(f: FolderResult): string {
  if (f.files === 0) return '-'
  if (f.inRunAll) return 'Yes'
  return f.folder === '02_tabletypes' ? 'With -WithTableTypes' : 'No - run by hand'
}

/** The house inline banner: a 12% wash of the status colour with the icon on the left. */
export function Banner({ tone, children }: { tone: 'error' | 'warning' | 'success'; children: React.ReactNode }) {
  const colour = tone === 'error' ? 'var(--st-miss)' : tone === 'warning' ? 'var(--st-diff)' : 'var(--st-same)'
  return (
    <div
      className="flex items-start gap-2 rounded-md px-3 py-2 text-[12px] leading-snug"
      style={{ background: `color-mix(in srgb, ${colour} 12%, transparent)`, color: colour }}
    >
      {tone === 'success' ? (
        <CircleCheck size={14} className="mt-px shrink-0" />
      ) : (
        <AlertTriangle size={14} className="mt-px shrink-0" />
      )}
      <div className="min-w-0">{children}</div>
    </div>
  )
}
