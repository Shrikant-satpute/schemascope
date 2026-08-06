import { useCallback, useEffect, useRef, useState } from 'react'
import { DiffEditor, type MonacoDiffEditor } from '@monaco-editor/react'
import type { Monaco } from '@monaco-editor/react'
import type { editor as MonacoApi } from 'monaco-editor/editor/editor.api'
import { ChevronDown, ChevronUp, Columns2, Copy, FileText, Table2, Check } from 'lucide-react'
import type { ObjectDetail } from '../types'
import type { DbColour } from '../dbColour'
import { STATUS } from '../status'
import { Button, EmptyState, Spinner } from './ui'
import { StructureGrid } from './StructureGrid'

const THEME_DARK = 'schemascope-dark'
const THEME_LIGHT = 'schemascope-light'

function defineThemes(monaco: Monaco) {
  monaco.editor.defineTheme(THEME_DARK, {
    base: 'vs-dark',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#12141b',
      'editorGutter.background': '#12141b',
      'editor.lineHighlightBackground': '#171a23',
      'editorLineNumber.foreground': '#4d566b',
      'editorLineNumber.activeForeground': '#9aa1b4',
      'diffEditor.insertedTextBackground': '#1baf7a26',
      'diffEditor.removedTextBackground': '#d03b3b26',
      'diffEditor.insertedLineBackground': '#1baf7a1a',
      'diffEditor.removedLineBackground': '#d03b3b1a',
      'diffEditorGutter.insertedLineBackground': '#1baf7a40',
      'diffEditorGutter.removedLineBackground': '#d03b3b40',
      'editorOverviewRuler.border': '#00000000',
    },
  })

  monaco.editor.defineTheme(THEME_LIGHT, {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {
      'editor.background': '#ffffff',
      'editorGutter.background': '#ffffff',
      'diffEditor.insertedTextBackground': '#0b907026',
      'diffEditor.removedTextBackground': '#c22f2f26',
      'diffEditor.insertedLineBackground': '#0b90701a',
      'diffEditor.removedLineBackground': '#c22f2f1a',
    },
  })
}

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

export function DiffPanel({
  detail,
  loading,
  theme,
  sourceColour,
  targetColours,
}: {
  detail: ObjectDetail | null
  loading: boolean
  theme: 'dark' | 'light'
  sourceColour: DbColour | null
  targetColours: DbColour[]
}) {
  const [targetIndex, setTargetIndex] = useState(0)
  const [view, setView] = useState<'text' | 'grid'>('text')
  const [inline, setInline] = useState(false)
  const [showUnchanged, setShowUnchanged] = useState(false)
  const [copied, setCopied] = useState<'source' | 'target' | null>(null)
  const [matches, setMatches] = useState<MatchCount | null>(null)
  const editorRef = useRef<MonacoDiffEditor | null>(null)
  const clearHighlightsRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    setTargetIndex(0)
    if (detail && !detail.isModule && detail.hasStructure) setView('grid')
    else setView('text')
  }, [detail?.key])

  // A new object means new text on both sides - anything left over from the
  // last one is meaningless.
  useEffect(() => {
    clearHighlightsRef.current?.()
    setMatches(null)
  }, [detail?.key, targetIndex])

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
    clearHighlightsRef.current = clear

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

    const listeners = panes.map((pane) =>
      pane.onDidChangeCursorSelection(() => {
        const selection = pane.getSelection()
        const model = pane.getModel()

        // One line only: dragging over a whole block is a read, not a lookup.
        if (!selection || !model || selection.isEmpty() ||
            selection.startLineNumber !== selection.endLineNumber) {
          clear()
          setMatches(null)
          return
        }

        paint(model.getValueInRange(selection))
      }),
    )

    return () => {
      listeners.forEach((l) => l.dispose())
      clearHighlightsRef.current = null
    }
  }, [])

  useEffect(() => () => clearHighlightsRef.current?.(), [])

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
          />
        ) : (
          <DiffEditor
            height="100%"
            language="sql"
            original={detail.sourceText}
            modified={target?.text ?? ''}
            theme={theme === 'dark' ? THEME_DARK : THEME_LIGHT}
            beforeMount={defineThemes}
            onMount={(editor, monaco) => {
              editorRef.current = editor
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
              const sub = editor.onDidUpdateDiff(() => {
                jump()
                sub.dispose()
              })
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
