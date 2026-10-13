import { useEffect, useState } from 'react'
import { AlertTriangle, ChevronDown, ChevronRight, Search, X } from 'lucide-react'
import type { ReferenceLine, ReferenceObject, ReferenceResult } from '../types'
import type { DbColour } from '../dbColour'
import { Spinner } from './ui'

/**
 * Who calls this?
 *
 * The tree beside the diff: one row per object that mentions the name, expandable
 * to the exact lines. Clicking a line opens that object next door, at that line.
 *
 * The counts are shown for every database at once. That is the part worth having -
 * if a name has nine callers in one database and seven in another, the difference
 * is the finding, and you would never see it looking at one side at a time.
 */
export function ReferencesPanel({
  result,
  loading,
  error,
  activeDb,
  onDbChange,
  onModeChange,
  onOpen,
  onClose,
  colourFor,
}: {
  result: ReferenceResult | null
  loading: boolean
  error: string | null
  activeDb: string
  onDbChange: (id: string) => void
  onModeChange: (mode: 'usedBy' | 'uses') => void
  onOpen: (object: ReferenceObject, line: ReferenceLine | null, databaseId: string) => void
  onClose: () => void
  colourFor: (databaseId: string) => DbColour | null
}) {
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())

  // A new search starts fully open again.
  useEffect(() => setCollapsed(new Set()), [result?.name, result?.mode])

  const database = result?.databases.find((d) => d.id === activeDb) ?? result?.databases[0] ?? null
  const definition = database?.objects.find((o) => o.isDefinition) ?? null
  const callers = database?.objects.filter((o) => !o.isDefinition) ?? []

  const toggle = (key: string) =>
    setCollapsed((c) => {
      const next = new Set(c)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })

  return (
    <div
      className="flex h-full w-80 shrink-0 flex-col border-r"
      style={{ borderColor: 'var(--line)', background: 'var(--surface)' }}
    >
      <div className="flex shrink-0 items-start gap-2 border-b px-3 py-2" style={{ borderColor: 'var(--line)' }}>
        <Search size={13} className="mt-1 shrink-0" style={{ color: 'var(--text-3)' }} />
        <div className="min-w-0 flex-1">
          <div className="truncate font-mono text-[12px] font-medium" title={result?.name}>
            {result?.name || 'References'}
          </div>
          {result && (
            <div className="text-[10.5px]" style={{ color: 'var(--text-3)' }}>
              found in {result.elapsedMs} ms
            </div>
          )}
        </div>
        <button
          onClick={onClose}
          className="shrink-0 rounded p-0.5 transition-colors hover:bg-[var(--raised)]"
          style={{ color: 'var(--text-3)' }}
          aria-label="Close references"
        >
          <X size={14} />
        </button>
      </div>

      {/* which direction */}
      <div className="flex shrink-0 gap-0.5 border-b p-1" style={{ borderColor: 'var(--line)' }}>
        {(
          [
            ['usedBy', 'Used by'],
            ['uses', 'Uses'],
          ] as const
        ).map(([mode, label]) => (
          <button
            key={mode}
            onClick={() => onModeChange(mode)}
            className="flex-1 rounded px-2 py-1 text-[12px] font-medium transition-colors"
            style={{
              background: result?.mode === mode ? 'var(--accent-soft)' : 'transparent',
              color: result?.mode === mode ? 'var(--accent)' : 'var(--text-3)',
            }}
          >
            {label}
          </button>
        ))}
      </div>

      {/* one chip per database - the counts side by side are the point */}
      {result && result.databases.length > 0 && (
        <div className="flex shrink-0 flex-wrap gap-1 border-b px-2 py-1.5" style={{ borderColor: 'var(--line)' }}>
          {result.databases.map((d) => {
            const colour = colourFor(d.id)
            const active = d.id === (database?.id ?? '')
            return (
              <button
                key={d.id}
                onClick={() => onDbChange(d.id)}
                className="inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-[11.5px] transition-colors"
                style={{
                  borderColor: active ? (colour?.ink ?? 'var(--accent)') : (colour?.line ?? 'var(--line)'),
                  background: active ? (colour?.tintStrong ?? 'var(--accent-soft)') : 'transparent',
                }}
                title={`${d.matchCount} matches in ${d.objectCount} objects`}
              >
                <span className="max-w-24 truncate">{d.label}</span>
                <span className="tabular-nums font-medium" style={{ color: colour?.ink }}>
                  {d.objectCount}
                </span>
              </button>
            )
          })}
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto">
        {loading && (
          <div className="flex items-center gap-2 px-3 py-3 text-[12px]" style={{ color: 'var(--text-2)' }}>
            <Spinner size={12} /> Searching every object...
          </div>
        )}

        {error && (
          <div className="flex items-start gap-2 px-3 py-3 text-[12px]" style={{ color: 'var(--st-miss)' }}>
            <AlertTriangle size={14} className="mt-px shrink-0" />
            {error}
          </div>
        )}

        {!loading && !error && database && database.objectCount === 0 && (
          <div className="px-3 py-4 text-[12px]" style={{ color: 'var(--text-2)' }}>
            {result?.mode === 'uses'
              ? 'This object does not reference anything else in the comparison.'
              : 'Nothing in this database refers to that name.'}
            <div className="mt-1 text-[11px]" style={{ color: 'var(--text-3)' }}>
              Check the other databases above - the answer often differs between them.
            </div>
          </div>
        )}

        {definition && (
          <Section title="Defined in">
            <ObjectRow
              object={definition}
              collapsed={collapsed.has(definition.key)}
              onToggle={() => toggle(definition.key)}
              onOpenObject={() => onOpen(definition, null, database!.id)}
              onOpenLine={(l) => onOpen(definition, l, database!.id)}
            />
          </Section>
        )}

        {callers.length > 0 && (
          <Section
            title={
              result?.mode === 'uses'
                ? `Uses ${callers.length} object${callers.length === 1 ? '' : 's'}`
                : `Referenced by ${callers.length} object${callers.length === 1 ? '' : 's'}`
            }
          >
            {callers.map((o) => (
              <ObjectRow
                key={o.key}
                object={o}
                collapsed={collapsed.has(o.key)}
                onToggle={() => toggle(o.key)}
                onOpenObject={() => onOpen(o, null, database!.id)}
                onOpenLine={(l) => onOpen(o, l, database!.id)}
              />
            ))}
          </Section>
        )}

        {result?.truncated && (
          <div className="px-3 py-2 text-[11px]" style={{ color: 'var(--st-diff)' }}>
            There were more matches than this list shows.
          </div>
        )}
      </div>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="border-b py-1" style={{ borderColor: 'var(--line-soft)' }}>
      <div
        className="px-3 py-1 text-[10.5px] font-semibold uppercase tracking-wide"
        style={{ color: 'var(--text-3)' }}
      >
        {title}
      </div>
      {children}
    </div>
  )
}

function ObjectRow({
  object,
  collapsed,
  onToggle,
  onOpenObject,
  onOpenLine,
}: {
  object: ReferenceObject
  collapsed: boolean
  onToggle: () => void
  onOpenObject: () => void
  onOpenLine: (line: ReferenceLine) => void
}) {
  return (
    <div>
      <div className="flex items-center gap-1 px-1.5 hover:bg-[var(--raised)]">
        <button
          onClick={onToggle}
          className="shrink-0 rounded p-0.5"
          style={{ color: 'var(--text-3)' }}
          aria-label={collapsed ? 'Show lines' : 'Hide lines'}
        >
          {collapsed ? <ChevronRight size={13} /> : <ChevronDown size={13} />}
        </button>

        <button
          onClick={onOpenObject}
          className="flex min-w-0 flex-1 items-center gap-2 py-1 text-left"
          title={`${object.schema}.${object.name} - ${object.typeLabel}`}
        >
          <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{object.name}</span>
          <span className="shrink-0 text-[10px]" style={{ color: 'var(--text-3)' }}>
            {object.typeLabel}
          </span>
          <span
            className="shrink-0 rounded px-1 text-[10.5px] font-semibold tabular-nums"
            style={{ background: 'var(--raised)', color: 'var(--text-2)' }}
          >
            {object.matchCount}
          </span>
        </button>
      </div>

      {!collapsed &&
        object.lines.map((l, i) => (
          <button
            key={`${l.line}-${i}`}
            onClick={() => onOpenLine(l)}
            className="flex w-full items-start gap-2 py-0.5 pl-7 pr-2 text-left transition-colors hover:bg-[var(--raised)]"
            title={`Open at line ${l.line}`}
          >
            <span
              className="shrink-0 pt-px text-right font-mono text-[10.5px] tabular-nums"
              style={{ color: 'var(--text-3)', minWidth: '2.2rem' }}
            >
              {l.line}
            </span>
            <span className="min-w-0 flex-1 truncate font-mono text-[11px]" style={{ color: 'var(--text-2)' }}>
              {l.text}
            </span>
          </button>
        ))}
    </div>
  )
}
