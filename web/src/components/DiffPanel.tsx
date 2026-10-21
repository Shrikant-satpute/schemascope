import { useCallback, useEffect, useRef, useState } from 'react'
import { DiffEditor, type MonacoDiffEditor } from '@monaco-editor/react'
import type { Monaco } from '@monaco-editor/react'
import type { editor as MonacoApi } from 'monaco-editor/editor/editor.api'
import { ChevronDown, ChevronUp, Columns2, Copy, FileCode, FileText, Table2, Check } from 'lucide-react'
import type { ObjectDetail } from '../types'
import type { DbColour } from '../dbColour'
import { STATUS } from '../status'
import { THEME_DARK, THEME_LIGHT, defineThemes } from '../monacoThemes'
import { Button, EmptyState, Spinner } from './ui'
import { StructureGrid } from './StructureGrid'

// ------------------------------------------------------------------
//  Match highlighting
// ------------------------------------------------------------------

/**
 * The usual word separators, minus @ # and $, because in T-SQL those start an
 * identifier: @start_date and #temp are one word, not two.
 */
const SQL_WORD_SEPARATORS = "`~!%^&*()-=+[{]}\\|;:'\",.<>/?"

/** Selecting a whole procedure body should not paint the entire file. */
const MAX_SELECTION = 120
const MAX_MATCHES = 2000

interface MatchCount {
  text: string
  left: number
  right: number
}

export type DiffSide = 'source' | 'target'

/** What was under the pointer when somebody right-clicked in the diff. */
export interface DiffContextInfo {
  x: number
  y: number
  side: DiffSide
  /** The word under the pointer, if any. */
  word: string
  /** The current selection, which wins over the word when there is one. */
  selection: string
}

export interface RevealTarget {
  line: number
  column?: number
  side: DiffSide
  /** Switch to this target tab first, when the hit is in a target. */
  targetIndex?: number
  /** Change this to jump again to the same place. */
  nonce: number
}

export function DiffPanel({
  detail,
  loading,
  theme,
  sourceColour,
  targetColours,
  onContextMenu,
  revealTarget,
  highlight,
  onGenerateScript,
  targetErrors,
  preferredTargetIndex,
}: {
  detail: ObjectDetail | null
  loading: boolean
  theme: 'dark' | 'light'
  sourceColour: DbColour | null
  targetColours: DbColour[]
  /** Raised on right-click; the workspace decides what the menu should offer. */
  onContextMenu?: (info: DiffContextInfo) => void
  /** Jump to a line, e.g. after clicking a search result. Bump the nonce to repeat. */
  revealTarget?: RevealTarget | null
  /** Kept lit even when nothing is selected - the term the references panel is showing. */
  highlight?: string | null
  /**
   * Owner only: script just this object against the target tab on show. The
   * argument is the index into the run's targets. Without it - a shared link -
   * the button and its shortcut do not exist.
   */
  onGenerateScript?: (targetIndex: number) => void
  /** Per target, why it could not be read, or null. Disables the button for that tab. */
  targetErrors?: (string | null)[]
  /** The target tab a new object opens on - the one the grid is narrowed to - instead of the first. */
  preferredTargetIndex?: number | null
}) {
  const [targetIndex, setTargetIndex] = useState(0)
  const [view, setView] = useState<'text' | 'grid'>('text')
  const [inline, setInline] = useState(false)
  const [showUnchanged, setShowUnchanged] = useState(false)
  const [copied, setCopied] = useState<'source' | 'target' | null>(null)
  const [matches, setMatches] = useState<MatchCount | null>(null)

  /** Bumped when Monaco is ready, so effects that need the editor can run again. */
  const [ready, setReady] = useState(0)

  const editorRef = useRef<MonacoDiffEditor | null>(null)
  const monacoRef = useRef<Monaco | null>(null)
  const paintRef = useRef<((text: string) => void) | null>(null)
  const restoreRef = useRef<(() => void) | null>(null)
  const flashRef = useRef<MonacoApi.IEditorDecorationsCollection | null>(null)
  const applyRevealRef = useRef<((afterModelChange?: boolean) => boolean) | null>(null)
  const appliedNonce = useRef<number | null>(null)
  /** Reset per object, so every object still lands on its first change. */
  const autoJumped = useRef(false)
  // Read inside the Monaco callbacks, which are created once and must not close
  // over a stale prop.
  const externalRef = useRef<string | null>(null)
  const contextRef = useRef(onContextMenu)
  contextRef.current = onContextMenu

  useEffect(() => {
    const preferred = preferredTargetIndex ?? 0
    setTargetIndex(detail && preferred < detail.targets.length ? preferred : 0)
    autoJumped.current = false
    if (detail && !detail.isModule && detail.hasStructure) setView('grid')
    else setView('text')
  }, [detail?.key])

  // A new object means new text on both sides. Anything selected there is
  // meaningless now, but a term the references panel is showing still applies.
  useEffect(() => {
    externalRef.current = highlight ?? null
    restoreRef.current?.()
  }, [highlight, detail?.key, targetIndex])

  /**
   * Select a word on either side and every other place it appears lights up -
   * in that pane and in the one opposite. Reading a diff is mostly asking
   * "where else does this column / variable / type turn up", and until now that
   * meant scrolling both sides by eye.
   */
  const attachHighlighting = useCallback((diff: MonacoDiffEditor, monaco: Monaco) => {
    const panes = [diff.getOriginalEditor(), diff.getModifiedEditor()]
    const collections = panes.map((p) => p.createDecorationsCollection())

    const options: MonacoApi.IModelDecorationOptions = {
      className: 'ss-match',
      overviewRuler: {
        color: '#f0a03288',
        position: monaco.editor.OverviewRulerLane.Center,
      },
      minimap: {
        color: '#f0a03288',
        position: monaco.editor.MinimapPosition.Inline,
      },
      stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
    }

    const clear = () => {
      try {
        collections.forEach((c) => c.clear())
      } catch {
        /* the editor is already gone - nothing left to un-highlight */
      }
    }

    /** Whole-word first, so "id" does not light up every "identity". */
    const findIn = (pane: MonacoApi.ICodeEditor, text: string, wordLike: boolean) => {
      const model = pane.getModel()
      if (!model) return []

      const search = (separators: string | null) =>
        model.findMatches(text, false, false, false, separators, false, MAX_MATCHES)

      const strict = wordLike ? search(SQL_WORD_SEPARATORS) : []
      // A part-word selection like "start_date" out of "@start_date" finds
      // nothing as a whole word. Fall back rather than look broken.
      return strict.length > 0 ? strict : search(null)
    }

    const paint = (raw: string) => {
      const text = raw.trim()
      if (!text || text.length > MAX_SELECTION) {
        clear()
        setMatches(null)
        return
      }

      const wordLike = /^[\w@#$]+$/.test(text)
      const found = panes.map((pane, i) => {
        const ranges = findIn(pane, text, wordLike)
        collections[i].set(ranges.map((m) => ({ range: m.range, options })))
        return ranges.length
      })

      setMatches({ text, left: found[0], right: found[1] })
    }

    /** Nothing selected falls back to whatever the references panel is showing. */
    const restore = () => {
      if (externalRef.current) paint(externalRef.current)
      else {
        clear()
        setMatches(null)
      }
    }

    paintRef.current = paint
    restoreRef.current = restore

    const listeners = panes.flatMap((pane, index) => {
      const side: DiffSide = index === 0 ? 'source' : 'target'

      const selectionListener = pane.onDidChangeCursorSelection(() => {
        const selection = pane.getSelection()
        const model = pane.getModel()

        // One line only: dragging over a whole block is a read, not a lookup.
        if (!selection || !model || selection.isEmpty() ||
            selection.startLineNumber !== selection.endLineNumber) {
          restore()
          return
        }

        paint(model.getValueInRange(selection))
      })

      // Monaco's own menu is off, so the browser event is ours to take. Asking
      // the editor what is under the pointer beats guessing from coordinates.
      const node = pane.getDomNode()
      const onMenu = (event: MouseEvent) => {
        if (!contextRef.current) return
        event.preventDefault()

        const model = pane.getModel()
        const selection = pane.getSelection()
        const position = pane.getTargetAtClientPoint(event.clientX, event.clientY)?.position

        const selected =
          model && selection && !selection.isEmpty() ? model.getValueInRange(selection) : ''
        const word = model && position ? (model.getWordAtPosition(position)?.word ?? '') : ''

        contextRef.current({
          x: event.clientX,
          y: event.clientY,
          side,
          word,
          selection: selected,
        })
      }

      node?.addEventListener('contextmenu', onMenu)

      return [
        selectionListener,
        { dispose: () => node?.removeEventListener('contextmenu', onMenu) },
      ]
    })

    return () => {
      listeners.forEach((l) => l.dispose())
      paintRef.current = null
      restoreRef.current = null
    }
  }, [])

  /**
   * Jump to a line somebody clicked in the references panel, and flash it so the
   * eye lands in the right place on a 600 line procedure.
   *
   * Reassigned every render so it always reads current props, and returns whether
   * it managed it. Three things call it, because any one of them alone misses a
   * case: the effect below (normal path), the mount callback (the editor did not
   * exist yet - searching from a matrix row without opening anything first), and
   * the diff-updated event (the models had not caught up).
   */
  applyRevealRef.current = (afterModelChange = false) => {
    const target = revealTarget
    const diff = editorRef.current
    const monaco = monacoRef.current

    if (!target || !diff || !monaco) return false

    // Monaco reports itself mounted before the text is in it, so an early jump
    // gets wiped when the content lands and the scroll resets to the top. The
    // diff-updated event is the moment the text is really there, so a jump that
    // is still fresh is allowed to run a second time from there.
    const settling = afterModelChange && Date.now() - target.nonce < 3000
    if (!settling && target.nonce === appliedNonce.current) return false

    // A hit in a target belongs to that tab, and lines only exist in the text view.
    if (target.targetIndex !== undefined && target.targetIndex !== targetIndex) {
      setTargetIndex(target.targetIndex)
      return false
    }
    if (view !== 'text') {
      setView('text')
      return false
    }

    const pane = target.side === 'source' ? diff.getOriginalEditor() : diff.getModifiedEditor()
    const model = pane?.getModel()
    if (!pane || !model || model.getLineCount() < target.line) return false

    // Opening a result loads a new object, and the editor's model catches up a
    // beat later. Scrolling now would move the *previous* object to line 34 and
    // then get reset when the text arrives - so wait until the model really is
    // showing what we were asked to jump into.
    const expected =
      target.side === 'source'
        ? detail?.sourceText
        : detail?.targets[target.targetIndex ?? targetIndex]?.text
    if (expected && model.getLineCount() !== expected.split('\n').length) return false

    appliedNonce.current = target.nonce
    // Monaco finishes computing the diff after this runs, and the convenience
    // jump rides in on that event. Claim it now or it scrolls this away.
    autoJumped.current = true

    pane.revealLineNearTop(target.line)
    pane.setPosition({ lineNumber: target.line, column: target.column ?? 1 })

    flashRef.current?.clear()
    flashRef.current = pane.createDecorationsCollection([
      {
        range: new monaco.Range(target.line, 1, target.line, 1),
        options: { isWholeLine: true, className: 'ss-flash-line' },
      },
    ])
    window.setTimeout(() => flashRef.current?.clear(), 1800)

    return true
  }

  useEffect(() => {
    applyRevealRef.current?.()
  }, [revealTarget, detail?.key, targetIndex, view, ready])

  useEffect(() => () => restoreRef.current?.(), [])

  // Keyboard: F8 / Shift+F8 walk through the changes, like a code review tool.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'F8' || !editorRef.current) return
      e.preventDefault()
      const action = e.shiftKey
        ? 'editor.action.diffReview.prev'
        : 'editor.action.diffReview.next'
      editorRef.current.getModifiedEditor()?.getAction(action)?.run()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  // Ctrl+Shift+G: the "Generate script" button, for the object and tab on show.
  // Not while typing, inside an editor, or with a dialog already open.
  const generateRef = useRef<(() => void) | null>(null)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!e.ctrlKey || !e.shiftKey || e.altKey || e.code !== 'KeyG') return
      const el = e.target as HTMLElement | null
      if (el && (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) || el.isContentEditable || el.closest('.monaco-editor'))) return
      if (document.querySelector('[aria-modal="true"]')) return
      if (!generateRef.current) return
      e.preventDefault()
      generateRef.current()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  generateRef.current = null

  if (loading) {
    return (
      <div className="flex h-full items-center justify-center gap-2 text-[13px]" style={{ background: 'var(--surface)', color: 'var(--text-2)' }}>
        <Spinner /> Loading definition...
      </div>
    )
  }

  if (!detail) {
    return (
      <div className="h-full" style={{ background: 'var(--surface)' }}>
        <EmptyState
          icon={<FileText size={26} />}
          title="Pick an object to see the difference"
          body="Choose any row above. Procedures and views open as a side-by-side diff with line numbers; tables open as a field grid."
        />
      </div>
    )
  }

  const target = detail.targets[targetIndex]
  const canGrid = detail.hasStructure && (target?.structure.length ?? 0) > 0

  // Why this tab has nothing to script, or null when it does.
  const targetError = targetErrors?.[targetIndex]
  const scriptBlocked = !target
    ? 'No target selected'
    : targetError
      ? `${target.label} could not be read: ${targetError}`
      : target.status === 'same'
        ? `Already the same in ${target.label}`
        : target.status === 'unavailable'
          ? (target.note ?? `This object cannot be read in ${target.label}`)
          : null
  if (onGenerateScript && !scriptBlocked) generateRef.current = () => onGenerateScript(targetIndex)

  async function copy(which: 'source' | 'target') {
    await navigator.clipboard.writeText(which === 'source' ? detail!.sourceText : target?.text ?? '')
    setCopied(which)
    setTimeout(() => setCopied(null), 1400)
  }

  return (
    <div className="flex h-full flex-col" style={{ background: 'var(--surface)' }}>
      {/* object header */}
      <div
        className="flex shrink-0 flex-wrap items-center gap-2 border-b px-3 py-1.5"
        style={{ borderColor: 'var(--line)' }}
      >
        <span className="text-[11px] font-medium uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
          {detail.typeLabel}
        </span>
        <span className="font-mono text-[13px] font-medium">{detail.fullName}</span>

        {detail.targets.length > 1 && (
          <div className="ml-2 flex gap-0.5 rounded-md p-0.5" style={{ background: 'var(--page)' }}>
            {detail.targets.map((t, i) => (
              <button
                key={t.id}
                onClick={() => setTargetIndex(i)}
                className="inline-flex items-center gap-1.5 rounded px-2 py-0.5 text-[11.5px] font-medium transition-colors"
                style={{
                  background:
                    i === targetIndex ? (targetColours[i]?.tintStrong ?? 'var(--surface)') : 'transparent',
                  color: i === targetIndex ? STATUS[t.status].colour : 'var(--text-3)',
                }}
                title={`${t.label} - ${STATUS[t.status].label}`}
              >
                <Swatch colour={targetColours[i]} />
                <span className="font-mono">{STATUS[t.status].symbol}</span> {t.label}
              </button>
            ))}
          </div>
        )}

        <div className="flex-1" />

        {view === 'text' && matches && (
          <span
            className="ss-fade inline-flex items-center gap-1.5 rounded px-2 py-0.5 text-[11.5px]"
            style={{ background: 'var(--raised)', color: 'var(--text-2)' }}
            title="Every place the selected text appears, on both sides"
          >
            <span aria-hidden className="inline-block h-2 w-2 rounded-sm ss-match-dot" />
            <span className="max-w-40 truncate font-mono">{matches.text}</span>
            <span className="tabular-nums">
              {matches.left} left / {matches.right} right
            </span>
          </span>
        )}

        {target && (
          <span className="flex items-center gap-2 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
            <span style={{ color: STATUS[target.status].colour }}>{STATUS[target.status].label}</span>
            {target.status === 'different' && (
              <span className="font-mono">
                <span style={{ color: 'var(--add)' }}>+{target.addedLines}</span>{' '}
                <span style={{ color: 'var(--del)' }}>-{target.removedLines}</span>
              </span>
            )}
          </span>
        )}

        {canGrid && (
          <div className="flex gap-0.5 rounded-md p-0.5" style={{ background: 'var(--page)' }}>
            <ViewToggle active={view === 'grid'} onClick={() => setView('grid')} icon={<Table2 size={13} />} label="Grid" />
            <ViewToggle active={view === 'text'} onClick={() => setView('text')} icon={<FileText size={13} />} label="Script" />
          </div>
        )}

        {view === 'text' && (
          <Button size="sm" variant="outline" onClick={() => setInline((v) => !v)} title="Toggle inline / side by side">
            <Columns2 size={13} /> {inline ? 'Inline' : 'Split'}
          </Button>
        )}

        {onGenerateScript && (
          // aria-disabled, not disabled: a blocked button stays focusable, so its reason (the title) reaches keyboard
          // and screen-reader users too.
          <Button
            size="sm"
            variant="outline"
            aria-disabled={!!scriptBlocked}
            className={scriptBlocked ? 'cursor-not-allowed opacity-40' : undefined}
            title={scriptBlocked ?? `Script just this object for ${target?.label} (Ctrl+Shift+G)`}
            onClick={() => !scriptBlocked && onGenerateScript(targetIndex)}
          >
            <FileCode size={13} />
            {detail.targets.length > 1 && target ? `Generate script for ${target.label}` : 'Generate script'}
          </Button>
        )}

        <Button size="sm" onClick={() => copy('source')} title="Copy the source definition">
          {copied === 'source' ? <Check size={13} /> : <Copy size={13} />}
        </Button>
      </div>

      {/* note line */}
      {target?.note && (
        <div
          className="shrink-0 px-3 py-1 text-[11.5px]"
          style={{ background: 'var(--surface-2)', color: 'var(--text-2)' }}
        >
          {target.note}
        </div>
      )}

      {/* body */}
      <div className="min-h-0 flex-1">
        {view === 'grid' && target ? (
          <StructureGrid
            sections={target.structure}
            sourceLabel={detail.sourceLabel}
            targetLabel={target.label}
            sourceColour={sourceColour}
            targetColour={targetColours[targetIndex] ?? null}
            showUnchanged={showUnchanged}
            onToggleUnchanged={setShowUnchanged}
            onNameContextMenu={(name, x, y) =>
              onContextMenu?.({ x, y, side: 'source', word: name, selection: '' })
            }
          />
        ) : (
          <DiffEditor
            height="100%"
            language={detail.language || 'sql'}
            original={detail.sourceText}
            modified={target?.text ?? ''}
            theme={theme === 'dark' ? THEME_DARK : THEME_LIGHT}
            beforeMount={defineThemes}
            onMount={(editor, monaco) => {
              editorRef.current = editor
              monacoRef.current = monaco
              attachHighlighting(editor, monaco)

              // Land on the first change rather than at line 1. On a 600 line
              // procedure the difference is usually nowhere near the top.
              const jump = () => {
                const changes = editor.getLineChanges?.()
                const first = changes?.[0]
                if (!first) return
                const line = first.modifiedStartLineNumber || first.originalStartLineNumber || 1
                editor.getModifiedEditor()?.revealLineNearTop(Math.max(1, line - 2))
              }

              // Fires whenever the models change and the diff has been recomputed -
              // the one moment we know both sides are really loaded.
              editor.onDidUpdateDiff(() => {
                restoreRef.current?.()

                // Somebody asked for a specific line. That always beats the
                // convenience jump, which would otherwise scroll it away again.
                if (applyRevealRef.current?.(true)) {
                  autoJumped.current = true
                  return
                }
                if (autoJumped.current) return
                autoJumped.current = true
                jump()
              })

              // The editor exists now; anything that gave up waiting can retry.
              setReady((r) => r + 1)
              applyRevealRef.current?.()
            }}
            options={{
              readOnly: true,
              renderSideBySide: !inline,
              lineNumbers: 'on',
              // Monaco's own highlighters only ever look at one pane. Ours
              // covers both, so leave it a clear field rather than stacking two
              // different washes on the same word.
              selectionHighlight: false,
              occurrencesHighlight: 'off',
              minimap: { enabled: true, renderCharacters: false },
              fontSize: 12,
              fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, monospace',
              scrollBeyondLastLine: false,
              renderOverviewRuler: true,
              diffWordWrap: 'off',
              ignoreTrimWhitespace: false,
              renderWhitespace: 'selection',
              smoothScrolling: true,
              automaticLayout: true,
              contextmenu: false,
              scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
            }}
          />
        )}
      </div>

      {/* footer legend */}
      <div
        className="flex shrink-0 items-center gap-4 border-t px-3 py-1 text-[11px]"
        style={{ borderColor: 'var(--line)', color: 'var(--text-3)' }}
      >
        <span className="inline-flex items-center gap-1.5">
          <Swatch colour={sourceColour} />
          Left: <b style={{ color: 'var(--text-2)' }}>{detail.sourceLabel}</b>
          {!detail.sourceExists && ' (not present)'}
        </span>
        <span className="inline-flex items-center gap-1.5">
          <Swatch colour={targetColours[targetIndex]} />
          Right: <b style={{ color: 'var(--text-2)' }}>{target?.label}</b>
          {target && !target.exists && ' (not present)'}
        </span>
        <div className="flex-1" />
        <span className="flex items-center gap-1">
          <ChevronDown size={11} /> F8 next change
          <ChevronUp size={11} className="ml-2" /> Shift+F8 previous
        </span>
      </div>
    </div>
  )
}

/** The database's identity colour, so the two sides of the diff are told apart at a glance. */
function Swatch({ colour }: { colour?: DbColour | null }) {
  if (!colour) return null
  return (
    <span
      aria-hidden
      className="inline-block h-2 w-2 shrink-0 rounded-full"
      style={{ background: colour.ink }}
    />
  )
}

function ViewToggle({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
}) {
  return (
    <button
      onClick={onClick}
      className="inline-flex items-center gap-1 rounded px-2 py-0.5 text-[11.5px] font-medium transition-colors"
      style={{
        background: active ? 'var(--surface)' : 'transparent',
        color: active ? 'var(--text)' : 'var(--text-3)',
      }}
    >
      {icon}
      {label}
    </button>
  )
}
