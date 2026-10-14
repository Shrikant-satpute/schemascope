import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Copy, FileCode2, Search, SquareDashedMousePointer } from 'lucide-react'
import type {
  CompareResult,
  CompareRow,
  ObjectDetail,
  ObjectStatus,
  ReferenceLine,
  ReferenceObject,
  ReferenceResult,
} from '../types'
import { dbColour, dbKey, type DbColour } from '../dbColour'
import { Dashboard } from './Dashboard'
import { MatrixGrid, filterRows, type MatrixFilters } from './MatrixGrid'
import { DiffPanel, type DiffContextInfo, type RevealTarget } from './DiffPanel'
import { ResultsToolbar } from './ResultsToolbar'
import { ContextMenu, type MenuEntry, type MenuState } from './ContextMenu'
import { ReferencesPanel } from './ReferencesPanel'

const EMPTY_FILTERS: MatrixFilters = {
  search: '',
  differencesOnly: true,
  types: new Set(),
  statuses: new Set(),
  targetIndex: null,
}

/**
 * Everything you do with a finished comparison: the dashboard, the filters, the
 * matrix and the diff.
 *
 * The owner window and a shared read-only link both render this, so what your
 * team sees behaves exactly like what you see. The only difference between them
 * is where the object text is fetched from, which is the one thing passed in.
 */
export function ResultsWorkspace({
  result,
  theme,
  loadObject,
  findReferences,
  findUses,
  onExport,
  exporting,
  onShare,
  sharing,
  onCreateScripts,
  onGenerateObjectScript,
  onFocusedTargetChange,
}: {
  result: CompareResult
  theme: 'dark' | 'light'
  loadObject: (key: string) => Promise<ObjectDetail | null>
  findReferences: (name: string) => Promise<ReferenceResult>
  findUses: (key: string) => Promise<ReferenceResult>
  /** Owner only. Viewers get no export, so the menu is not rendered for them. */
  onExport?: (format: 'html' | 'csv' | 'md' | 'sql', differencesOnly: boolean) => void
  exporting?: boolean
  onShare?: () => void
  sharing?: boolean
  /**
   * Owner only, like export: opens "Create scripts" for one target. The
   * argument is the index into `result.targets`. Viewers never pass it, so
   * the tile button does not exist on a shared link.
   */
  onCreateScripts?: (targetIndex: number) => void
  /**
   * Owner only: the diff panel's "Generate script" for one object (its row
   * key) against one target (index into `result.targets`). Viewers never pass
   * it, so the button does not exist on a shared link.
   */
  onGenerateObjectScript?: (key: string, targetIndex: number) => void
  /** Owner only: which target the grid is narrowed to, so a swap can preselect it. */
  onFocusedTargetChange?: (targetIndex: number | null) => void
}) {
  const [filters, setFilters] = useState<MatrixFilters>(EMPTY_FILTERS)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [detail, setDetail] = useState<ObjectDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [dashCollapsed, setDashCollapsed] = useState(false)

  const [splitPct, setSplitPct] = useState(52)
  const dragging = useRef(false)
  const splitRef = useRef<HTMLDivElement>(null)

  const [menu, setMenu] = useState<MenuState | null>(null)
  const [references, setReferences] = useState<ReferenceResult | null>(null)
  const [refsLoading, setRefsLoading] = useState(false)
  const [refsError, setRefsError] = useState<string | null>(null)
  const [refsDb, setRefsDb] = useState('source')
  const [reveal, setReveal] = useState<RevealTarget | null>(null)

  const rows = useMemo(() => filterRows(result, filters), [result, filters])

  useEffect(() => onFocusedTargetChange?.(filters.targetIndex), [filters.targetIndex, onFocusedTargetChange])

  // One tint per database, worked out once and handed down, so the dashboard
  // tile, the matrix column and the diff tab all agree on what colour a
  // database is.
  const sourceColour = useMemo(
    () => dbColour(dbKey(result.source.server, result.source.database)),
    [result],
  )
  const targetColours = useMemo(
    () => result.targets.map((t) => dbColour(dbKey(t.server, t.database))),
    [result],
  )
  const targetErrors = useMemo(
    () => result.targets.map((t) => (t.failed ? (t.error ?? 'the read failed') : null)),
    [result],
  )

  const selectRow = useCallback(
    async (row: CompareRow) => {
      setSelectedKey(row.key)
      setDetailLoading(true)
      try {
        setDetail(await loadObject(row.key))
      } catch {
        setDetail(null)
      } finally {
        setDetailLoading(false)
      }
    },
    [loadObject],
  )

  // ---------------- where is this used ----------------

  /** Every object in the run, by bare name, so a word can be recognised as one. */
  const objectsByName = useMemo(() => {
    const map = new Map<string, CompareRow>()
    for (const row of result.rows) map.set(row.name.toLowerCase(), row)
    return map
  }, [result])

  const runSearch = useCallback(
    async (work: () => Promise<ReferenceResult>) => {
      setRefsLoading(true)
      setRefsError(null)
      setReferences(null)
      try {
        const found = await work()
        setReferences(found)
        // Land on the database with something to show rather than an empty tree.
        const best = found.databases.find((d) => d.objectCount > 0) ?? found.databases[0]
        setRefsDb(best?.id ?? 'source')
      } catch (e) {
        setRefsError(e instanceof Error ? e.message : String(e))
      } finally {
        setRefsLoading(false)
      }
    },
    [],
  )

  const searchReferences = useCallback(
    (name: string) => void runSearch(() => findReferences(name)),
    [runSearch, findReferences],
  )

  const searchUses = useCallback(
    (key: string) => void runSearch(() => findUses(key)),
    [runSearch, findUses],
  )

  /** Open a search result: load the object, then jump to the line inside it. */
  const openReference = useCallback(
    async (object: ReferenceObject, line: ReferenceLine | null, databaseId: string) => {
      const row = result.rows.find((r) => r.key === object.key)
      if (row) await selectRow(row)
      else setDetail(await loadObject(object.key).catch(() => null))

      if (!line) return
      setReveal({
        line: line.line,
        column: line.column,
        side: databaseId === 'source' ? 'source' : 'target',
        targetIndex: databaseId === 'source' ? undefined : Number(databaseId),
        // Doubles as the time of the click: the diff panel uses it to tell a jump
        // it is still settling from an old one it should leave alone.
        nonce: Date.now(),
      })
    },
    [result, selectRow, loadObject],
  )

  /** Right-click anywhere: same menu, built from whatever is under the pointer. */
  const openMenu = useCallback(
    (x: number, y: number, term: string, row?: CompareRow) => {
      const clean = term.trim()
      const known = objectsByName.get(clean.toLowerCase())
      const entries: MenuEntry[] = []

      if (clean) {
        entries.push({
          label: `Copy "${clean.length > 28 ? `${clean.slice(0, 28)}...` : clean}"`,
          hint: 'Ctrl+C',
          icon: <Copy size={13} />,
          onSelect: () => void navigator.clipboard.writeText(clean).catch(() => undefined),
        })
      }

      if (detail) {
        entries.push({
          label: 'Copy whole object',
          icon: <FileCode2 size={13} />,
          onSelect: () => void navigator.clipboard.writeText(detail.sourceText).catch(() => undefined),
        })
      }

      if (entries.length > 0) entries.push('separator')

      entries.push({
        label: 'Find references',
        hint: 'where is it used',
        icon: <Search size={13} />,
        disabled: clean.length < 3,
        onSelect: () => searchReferences(clean),
      })

      const subject = row ?? known
      if (subject) {
        entries.push({
          label: 'Find what it uses',
          icon: <SquareDashedMousePointer size={13} />,
          onSelect: () => searchUses(subject.key),
        })
        if (!row) {
          entries.push({
            label: `Open ${subject.name}`,
            onSelect: () => void selectRow(subject),
          })
        }
      }

      setMenu({ x, y, entries })
    },
    [detail, objectsByName, searchReferences, searchUses, selectRow],
  )

  const onDiffContextMenu = useCallback(
    (info: DiffContextInfo) => openMenu(info.x, info.y, info.selection || info.word),
    [openMenu],
  )

  const colourFor = useCallback(
    (databaseId: string): DbColour | null =>
      databaseId === 'source' ? sourceColour : (targetColours[Number(databaseId)] ?? null),
    [sourceColour, targetColours],
  )

  // Arrow keys walk the matrix without leaving the keyboard.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (rows.length === 0) return
      const tag = (e.target as HTMLElement | null)?.tagName
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
      if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return

      e.preventDefault()
      const i = rows.findIndex((r) => r.key === selectedKey)
      const next = e.key === 'ArrowDown' ? Math.min(rows.length - 1, i + 1) : Math.max(0, i - 1)
      if (rows[next]) void selectRow(rows[next])
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [rows, selectedKey, selectRow])

  useEffect(() => {
    const move = (e: MouseEvent) => {
      if (!dragging.current || !splitRef.current) return
      const box = splitRef.current.getBoundingClientRect()
      const pct = ((e.clientY - box.top) / box.height) * 100
      setSplitPct(Math.min(85, Math.max(15, pct)))
    }
    const up = () => {
      dragging.current = false
      document.body.style.userSelect = ''
    }
    window.addEventListener('mousemove', move)
    window.addEventListener('mouseup', up)
    return () => {
      window.removeEventListener('mousemove', move)
      window.removeEventListener('mouseup', up)
    }
  }, [])

  return (
    <>
      <Dashboard
        result={result}
        sourceColour={sourceColour}
        targetColours={targetColours}
        collapsed={dashCollapsed}
        onToggle={() => setDashCollapsed((v) => !v)}
        onFocusTarget={(i) => setFilters((f) => ({ ...f, targetIndex: i, differencesOnly: true }))}
        onFilterStatus={(i, s: ObjectStatus) =>
          setFilters((f) => ({ ...f, targetIndex: i, statuses: new Set([s]), differencesOnly: true }))
        }
        onCreateScripts={onCreateScripts}
      />

      <ResultsToolbar
        result={result}
        filters={filters}
        onChange={setFilters}
        shownCount={rows.length}
        onExport={onExport ? (format) => onExport(format, filters.differencesOnly) : undefined}
        exporting={exporting}
        onShare={onShare}
        sharing={sharing}
      />

      <div ref={splitRef} className="flex min-h-0 flex-1 flex-col">
        <div style={{ height: `${splitPct}%` }} className="min-h-0">
          <MatrixGrid
            result={result}
            rows={rows}
            targetColours={targetColours}
            selectedKey={selectedKey}
            onSelect={selectRow}
            onRowContextMenu={(row, x, y) => openMenu(x, y, row.name, row)}
          />
        </div>

        <div
          onMouseDown={() => {
            dragging.current = true
            document.body.style.userSelect = 'none'
          }}
          className="group flex h-1.5 shrink-0 cursor-row-resize items-center justify-center"
          style={{ background: 'var(--line-soft)' }}
        >
          <span
            className="h-0.5 w-10 rounded-full transition-colors group-hover:bg-[var(--accent)]"
            style={{ background: 'var(--line)' }}
          />
        </div>

        <div className="flex min-h-0 flex-1">
          {(references || refsLoading || refsError) && (
            <ReferencesPanel
              result={references}
              loading={refsLoading}
              error={refsError}
              activeDb={refsDb}
              onDbChange={setRefsDb}
              onModeChange={(mode) => {
                if (mode === references?.mode) return
                if (mode === 'uses') {
                  if (detail) searchUses(detail.key)
                } else if (references?.name) {
                  searchReferences(references.name)
                }
              }}
              onOpen={openReference}
              onClose={() => {
                setReferences(null)
                setRefsError(null)
              }}
              colourFor={colourFor}
            />
          )}

          <div className="min-w-0 flex-1">
            <DiffPanel
              detail={detail}
              loading={detailLoading}
              theme={theme}
              sourceColour={sourceColour}
              targetColours={targetColours}
              onContextMenu={onDiffContextMenu}
              revealTarget={reveal}
              highlight={references?.mode === 'usedBy' ? (references.name ?? null) : null}
              onGenerateScript={
                onGenerateObjectScript && detail
                  ? (targetIndex) => onGenerateObjectScript(detail.key, targetIndex)
                  : undefined
              }
              targetErrors={targetErrors}
            />
          </div>
        </div>
      </div>

      <ContextMenu state={menu} onClose={() => setMenu(null)} />
    </>
  )
}
