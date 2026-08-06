import { useState } from 'react'
import { Download, Filter, Search, X } from 'lucide-react'
import type { CompareResult, DbObjectType, ObjectStatus } from '../types'
import { STATUS, STATUS_ORDER } from '../status'
import { Button, Input } from './ui'
import type { MatrixFilters } from './MatrixGrid'
import { Legend } from './MatrixGrid'

export function ResultsToolbar({
  result,
  filters,
  onChange,
  shownCount,
  onExport,
  exporting,
}: {
  result: CompareResult
  filters: MatrixFilters
  onChange: (f: MatrixFilters) => void
  shownCount: number
  onExport: (format: 'html' | 'csv' | 'md' | 'sql') => void
  exporting: boolean
}) {
  const [openMenu, setOpenMenu] = useState<'types' | 'status' | 'export' | null>(null)

  const set = (patch: Partial<MatrixFilters>) => onChange({ ...filters, ...patch })

  const toggleSet = <T,>(s: Set<T>, v: T) => {
    const next = new Set(s)
    if (next.has(v)) next.delete(v)
    else next.add(v)
    return next
  }

  const activeFilters =
    (filters.differencesOnly ? 0 : 0) +
    filters.types.size +
    filters.statuses.size +
    (filters.targetIndex !== null ? 1 : 0)

  return (
    <div
      className="flex shrink-0 flex-wrap items-center gap-2 border-b px-3 py-1.5"
      style={{ borderColor: 'var(--line)', background: 'var(--surface-2)' }}
    >
      <div className="relative">
        <Search size={13} className="absolute left-2 top-1/2 -translate-y-1/2" style={{ color: 'var(--text-3)' }} />
        <Input
          value={filters.search}
          onChange={(e) => set({ search: e.target.value })}
          placeholder="Filter objects..."
          className="w-56 pl-7"
        />
        {filters.search && (
          <button
            onClick={() => set({ search: '' })}
            className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded p-0.5 hover:bg-[var(--raised)]"
            aria-label="Clear search"
          >
            <X size={12} />
          </button>
        )}
      </div>

      <label className="flex cursor-pointer items-center gap-1.5 text-[12.5px]">
        <input
          type="checkbox"
          checked={filters.differencesOnly}
          onChange={(e) => set({ differencesOnly: e.target.checked })}
        />
        Differences only
      </label>

      {/* type filter */}
      <Dropdown
        open={openMenu === 'types'}
        onToggle={() => setOpenMenu(openMenu === 'types' ? null : 'types')}
        label="Types"
        count={filters.types.size}
      >
        {result.byType.map((b) => (
          <label key={b.type} className="flex cursor-pointer items-center gap-2 px-2.5 py-1 text-[12.5px] hover:bg-[var(--raised)]">
            <input
              type="checkbox"
              checked={filters.types.has(b.type)}
              onChange={() => set({ types: toggleSet(filters.types, b.type as DbObjectType) })}
            />
            <span className="flex-1">{b.label}</span>
            <span className="tabular-nums" style={{ color: 'var(--text-3)' }}>
              {b.withDifferences}/{b.total}
            </span>
          </label>
        ))}
        {filters.types.size > 0 && (
          <button
            className="w-full px-2.5 py-1 text-left text-[12px] hover:bg-[var(--raised)]"
            style={{ color: 'var(--accent)' }}
            onClick={() => set({ types: new Set() })}
          >
            Clear
          </button>
        )}
      </Dropdown>

      {/* status filter */}
      <Dropdown
        open={openMenu === 'status'}
        onToggle={() => setOpenMenu(openMenu === 'status' ? null : 'status')}
        label="Status"
        count={filters.statuses.size}
      >
        {STATUS_ORDER.map((s) => (
          <label key={s} className="flex cursor-pointer items-center gap-2 px-2.5 py-1 text-[12.5px] hover:bg-[var(--raised)]">
            <input
              type="checkbox"
              checked={filters.statuses.has(s)}
              onChange={() => set({ statuses: toggleSet(filters.statuses, s as ObjectStatus) })}
            />
            <span className="w-4 text-center font-mono font-semibold" style={{ color: STATUS[s].colour }}>
              {STATUS[s].symbol}
            </span>
            <span className="flex-1">{STATUS[s].label}</span>
          </label>
        ))}
        {filters.statuses.size > 0 && (
          <button
            className="w-full px-2.5 py-1 text-left text-[12px] hover:bg-[var(--raised)]"
            style={{ color: 'var(--accent)' }}
            onClick={() => set({ statuses: new Set() })}
          >
            Clear
          </button>
        )}
      </Dropdown>

      {/* target focus */}
      {result.targets.length > 1 && (
        <select
          value={filters.targetIndex ?? ''}
          onChange={(e) => set({ targetIndex: e.target.value === '' ? null : Number(e.target.value) })}
          className="rounded-md border px-2 py-1 text-[12.5px]"
          style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
        >
          <option value="">All targets</option>
          {result.targets.map((t, i) => (
            <option key={t.id} value={i}>
              Only {t.label}
            </option>
          ))}
        </select>
      )}

      {activeFilters > 0 && (
        <Button
          size="sm"
          onClick={() => set({ types: new Set(), statuses: new Set(), targetIndex: null, search: '' })}
          style={{ color: 'var(--accent)' }}
        >
          Reset filters
        </Button>
      )}

      <span className="text-[12px] tabular-nums" style={{ color: 'var(--text-3)' }}>
        {shownCount.toLocaleString()} of {result.totalRows.toLocaleString()}
      </span>

      <div className="flex-1" />

      <Legend />

      <Dropdown
        open={openMenu === 'export'}
        onToggle={() => setOpenMenu(openMenu === 'export' ? null : 'export')}
        label="Export"
        icon={<Download size={13} />}
        align="right"
        disabled={exporting}
      >
        {[
          { f: 'html' as const, label: 'HTML report', hint: 'Standalone page you can send to anyone' },
          { f: 'csv' as const, label: 'CSV / Excel', hint: 'One row per object' },
          { f: 'md' as const, label: 'Markdown', hint: 'Drops into a wiki or a PR description' },
          { f: 'sql' as const, label: 'SQL files...', hint: 'One .sql per object, into a folder' },
        ].map((x) => (
          <button
            key={x.f}
            onClick={() => {
              setOpenMenu(null)
              onExport(x.f)
            }}
            className="block w-full px-2.5 py-1.5 text-left hover:bg-[var(--raised)]"
          >
            <span className="block text-[12.5px]">{x.label}</span>
            <span className="block text-[11px]" style={{ color: 'var(--text-3)' }}>
              {x.hint}
            </span>
          </button>
        ))}
      </Dropdown>
    </div>
  )
}

function Dropdown({
  open,
  onToggle,
  label,
  count,
  icon,
  children,
  align = 'left',
  disabled,
}: {
  open: boolean
  onToggle: () => void
  label: string
  count?: number
  icon?: React.ReactNode
  children: React.ReactNode
  align?: 'left' | 'right'
  disabled?: boolean
}) {
  return (
    <div className="relative">
      <Button size="sm" variant="outline" onClick={onToggle} disabled={disabled}>
        {icon ?? <Filter size={13} />} {label}
        {count ? (
          <span
            className="rounded px-1 text-[10.5px] font-semibold"
            style={{ background: 'var(--accent-soft)', color: 'var(--accent)' }}
          >
            {count}
          </span>
        ) : null}
      </Button>

      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={onToggle} />
          <div
            className="ss-fade absolute z-20 mt-1 max-h-80 min-w-56 overflow-y-auto rounded-md border py-1 shadow-xl"
            style={{
              background: 'var(--surface)',
              borderColor: 'var(--line)',
              [align]: 0,
            }}
          >
            {children}
          </div>
        </>
      )}
    </div>
  )
}
