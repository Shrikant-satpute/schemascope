import { useMemo, useRef } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import type { CompareResult, CompareRow, ObjectStatus } from '../types'
import type { DbColour } from '../dbColour'
import { STATUS, cellText } from '../status'
import { EmptyState } from './ui'
import { SearchX } from 'lucide-react'

export interface MatrixFilters {
  search: string
  differencesOnly: boolean
  types: Set<string>
  statuses: Set<ObjectStatus>
  targetIndex: number | null
}

/**
 * The search box works like SQL's LIKE '%...%': part of a name is enough.
 * Words split by spaces must all appear, in any order ("live param").
 * % or * stands for any run of characters ("EY%Logs"), ? for one character.
 * _ stays a plain underscore - object names are full of them.
 * Brackets are ignored, so a name pasted from SSMS ([dbo].[X]) still matches.
 */
function searchMatcher(search: string): ((hay: string) => boolean) | null {
  const words = search.toLowerCase().replace(/[[\]]/g, '').split(/\s+/).filter(Boolean)
  if (words.length === 0) return null

  const tests = words.map((word) => {
    if (!/[%*?]/.test(word)) return (hay: string) => hay.includes(word)
    const pattern = word
      .replace(/[%*]+/g, '%')
      .split('')
      .map((c) => (c === '%' ? '.*' : c === '?' ? '.' : c.replace(/[.+^${}()|\\/]/g, '\\$&')))
      .join('')
    const rx = new RegExp(pattern)
    return (hay: string) => rx.test(hay)
  })
  return (hay) => tests.every((test) => test(hay))
}

export function filterRows(result: CompareResult, f: MatrixFilters): CompareRow[] {
  const matches = searchMatcher(f.search)

  return result.rows.filter((row) => {
    if (f.differencesOnly && !row.hasDifference) return false
    if (f.types.size > 0 && !f.types.has(row.type)) return false

    if (f.targetIndex !== null) {
      const cell = row.cells[f.targetIndex]
      if (!cell) return false

      // Focusing a target normally means "show me what drifted here", so the
      // identical rows drop out. An explicit status filter overrules that -
      // otherwise asking for Same in one target could only ever return nothing.
      if (f.statuses.size === 0 && cell.status === 'same') return false
    }

    if (f.statuses.size > 0) {
      const cells = f.targetIndex !== null ? [row.cells[f.targetIndex]] : row.cells
      if (!cells.some((c) => c && f.statuses.has(c.status))) return false
    }

    if (matches && !matches(`${row.schema}.${row.name} ${row.typeLabel}`.toLowerCase())) return false

    return true
  })
}

const ROW_HEIGHT = 26

export function MatrixGrid({
  result,
  rows,
  targetColours,
  selectedKey,
  onSelect,
  onRowContextMenu,
}: {
  result: CompareResult
  rows: CompareRow[]
  targetColours: DbColour[]
  selectedKey: string | null
  onSelect: (row: CompareRow) => void
  /** Right-click a row to ask about it without opening it first. */
  onRowContextMenu?: (row: CompareRow, x: number, y: number) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 18,
  })

  // Group headers: show the type name the first time it appears.
  const firstOfType = useMemo(() => {
    const seen = new Set<string>()
    const map = new Set<number>()
    rows.forEach((r, i) => {
      if (!seen.has(r.type)) {
        seen.add(r.type)
        map.add(i)
      }
    })
    return map
  }, [rows])

  // Object names get a generous but capped column, then a spacer, so the target
  // columns sit near the names instead of being flung to the far edge of a wide
  // monitor when there is only one target.
  const targetColWidth = 132

  if (rows.length === 0) {
    return (
      <div className="h-full" style={{ background: 'var(--surface)' }}>
        <EmptyState
          icon={<SearchX size={28} />}
          title="Nothing matches these filters"
          body="Try clearing the search box, or switch off 'Differences only' to see everything."
        />
      </div>
    )
  }

  return (
    <div className="flex h-full flex-col" style={{ background: 'var(--surface)' }}>
      {/* header */}
      <div
        className="flex shrink-0 border-b text-[10.5px] font-semibold uppercase tracking-wide"
        style={{ borderColor: 'var(--line)', color: 'var(--text-3)', background: 'var(--surface-2)' }}
      >
        <div className="w-28 shrink-0 px-3 py-1.5">Type</div>
        <div className="max-w-[640px] flex-1 px-2 py-1.5">Object</div>
        <div className="flex-1" />
        {result.targets.map((t, i) => {
          const colour = targetColours[i]
          return (
            <div
              key={t.id}
              className="shrink-0 truncate border-l px-2 py-1.5 text-center"
              style={{
                width: targetColWidth,
                borderColor: 'var(--line-soft)',
                background: colour?.tintStrong,
                color: colour?.ink,
                boxShadow: colour ? `inset 0 -2px 0 ${colour.line}` : undefined,
              }}
              title={`${t.label} - ${t.server}/${t.database}`}
            >
              {t.label}
            </div>
          )
        })}
      </div>

      {/* rows */}
      <div ref={scrollRef} className="flex-1 overflow-auto">
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
          {virtualizer.getVirtualItems().map((v) => {
            const row = rows[v.index]
            const selected = row.key === selectedKey
            const showType = firstOfType.has(v.index)

            return (
              <div
                key={row.key}
                onClick={() => onSelect(row)}
                onContextMenu={(e) => {
                  if (!onRowContextMenu) return
                  e.preventDefault()
                  onRowContextMenu(row, e.clientX, e.clientY)
                }}
                className="absolute left-0 flex w-full cursor-pointer items-center text-[12.5px]"
                style={{
                  height: v.size,
                  transform: `translateY(${v.start}px)`,
                  background: selected ? 'var(--accent-soft)' : undefined,
                  borderLeft: `2px solid ${selected ? 'var(--accent)' : 'transparent'}`,
                }}
                onMouseEnter={(e) => {
                  if (!selected) e.currentTarget.style.background = 'var(--raised)'
                }}
                onMouseLeave={(e) => {
                  if (!selected) e.currentTarget.style.background = ''
                }}
              >
                <div
                  className="w-28 shrink-0 truncate px-3 text-[11.5px]"
                  style={{ color: showType ? 'var(--text-2)' : 'transparent' }}
                >
                  {row.typeLabel}
                </div>

                <div className="min-w-0 max-w-[640px] flex-1 truncate px-2 font-mono text-[12px]">
                  <span style={{ color: 'var(--text-3)' }}>{row.schema}.</span>
                  <span>{row.name}</span>
                </div>

                <div className="flex-1" />

                {row.cells.map((cell, i) => {
                  const style = STATUS[cell.status]
                  return (
                    <div
                      key={i}
                      className="shrink-0 self-stretch border-l px-2 text-center font-mono text-[11.5px] tabular-nums"
                      style={{
                        width: targetColWidth,
                        lineHeight: `${ROW_HEIGHT}px`,
                        borderColor: 'var(--line-soft)',
                        color: style.colour,
                        // An alpha wash, so the hover and selection highlights
                        // underneath still come through the column tint.
                        background: targetColours[i]?.tint,
                      }}
                      title={`${result.targets[i]?.label}: ${style.label}${
                        cell.note ? ` - ${cell.note}` : ''
                      }${cell.changedLines ? ` (+${cell.addedLines} -${cell.removedLines} lines)` : ''}`}
                    >
                      {cellText(cell.status, cell.changedLines)}
                    </div>
                  )
                })}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}

export function Legend() {
  return (
    <div className="flex flex-wrap items-center gap-x-3.5 gap-y-1 text-[11px]" style={{ color: 'var(--text-3)' }}>
      {(['same', 'formattingOnly', 'different', 'missingInTarget', 'onlyInTarget', 'unavailable'] as ObjectStatus[]).map(
        (s) => (
          <span key={s} className="inline-flex items-center gap-1" title={STATUS[s].description}>
            <span className="font-mono font-semibold" style={{ color: STATUS[s].colour }}>
              {STATUS[s].symbol}
            </span>
            {STATUS[s].label}
          </span>
        ),
      )}
    </div>
  )
}
