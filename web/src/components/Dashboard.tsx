import { AlertTriangle, ChevronDown, ChevronUp, GitCompareArrows, Clock } from 'lucide-react'
import { useState } from 'react'
import type { CompareResult, ObjectStatus } from '../types'
import type { DbColour } from '../dbColour'
import { STATUS } from '../status'

/**
 * Stat tiles, not donuts.
 *
 * Each target's headline is a single number - how much of the source it still
 * matches - so a hero figure plus a meter reads faster than any pie. The
 * breakdown underneath is a stacked meter with a 2px gap between segments, and
 * every count is labelled in words as well as coloured.
 */
export function Dashboard({
  result,
  sourceColour,
  targetColours,
  onFocusTarget,
  onFilterStatus,
  collapsed,
  onToggle,
}: {
  result: CompareResult
  sourceColour: DbColour | null
  targetColours: DbColour[]
  onFocusTarget: (index: number) => void
  onFilterStatus: (index: number, status: ObjectStatus) => void
  collapsed: boolean
  onToggle: () => void
}) {
  const worstFirst = [...result.targets]
    .map((t, i) => ({ t, i }))
    .sort((a, b) => a.t.matchPercent - b.t.matchPercent)

  return (
    <div className="border-b" style={{ borderColor: 'var(--line)', background: 'var(--surface)' }}>
      <div className="flex items-center gap-3 px-4 pt-2.5">
        <h2 className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
          Drift dashboard
        </h2>

        <div className="flex items-center gap-3 text-[11.5px]" style={{ color: 'var(--text-2)' }}>
          <span
            className="inline-flex items-center gap-1.5 rounded px-1.5 py-px"
            style={{ background: sourceColour?.tint, border: `1px solid ${sourceColour?.line ?? 'transparent'}` }}
            title={`Source: ${result.source.server} / ${result.source.database}`}
          >
            <b style={{ color: 'var(--text)' }}>{result.source.objectCount.toLocaleString()}</b> objects in{' '}
            {result.source.label}
          </span>
          <span className="inline-flex items-center gap-1">
            <Clock size={11} />
            {result.totalMs} ms
          </span>
          {result.singleTargetDrift > 0 && (
            <span
              className="inline-flex items-center gap-1 rounded px-1.5 py-px"
              style={{ background: 'var(--raised)' }}
              title="Objects that differ in exactly one target. Usually a local customisation rather than a missed deployment."
            >
              <GitCompareArrows size={11} />
              {result.singleTargetDrift} drifted in one target only
            </span>
          )}
        </div>

        <div className="flex-1" />

        <button
          onClick={onToggle}
          className="rounded p-1 transition-colors hover:bg-[var(--raised)]"
          style={{ color: 'var(--text-3)' }}
          aria-label={collapsed ? 'Show dashboard' : 'Hide dashboard'}
        >
          {collapsed ? <ChevronDown size={15} /> : <ChevronUp size={15} />}
        </button>
      </div>

      {!collapsed && (
        <div className="flex gap-3 overflow-x-auto px-4 pb-3 pt-2.5">
          {worstFirst.map(({ t, i }) => (
            <TargetTile
              key={t.id}
              index={i}
              target={t}
              colour={targetColours[i] ?? null}
              onFocus={() => onFocusTarget(i)}
              onFilterStatus={(s) => onFilterStatus(i, s)}
            />
          ))}

          <TypeBreakdown result={result} />
        </div>
      )}

      {!collapsed &&
        result.warnings.map((w, i) => (
          <div
            key={i}
            className="mx-4 mb-2.5 flex items-start gap-2 rounded-md px-3 py-1.5 text-[12px]"
            style={{
              background: 'color-mix(in srgb, var(--st-diff) 12%, transparent)',
              color: 'var(--st-diff)',
            }}
          >
            <AlertTriangle size={13} className="mt-px shrink-0" />
            {w}
          </div>
        ))}
    </div>
  )
}

function TargetTile({
  target,
  colour,
  onFocus,
  onFilterStatus,
}: {
  index: number
  target: CompareResult['targets'][number]
  colour: DbColour | null
  onFocus: () => void
  onFilterStatus: (s: ObjectStatus) => void
}) {
  if (target.failed) {
    return (
      <div
        className="min-w-60 shrink-0 rounded-lg border px-3.5 py-2.5"
        style={{ borderColor: 'var(--st-miss)', background: 'var(--surface-2)' }}
      >
        <div className="truncate text-[12.5px] font-semibold">{target.label}</div>
        <div className="mt-1.5 flex items-center gap-1.5 text-[12px]" style={{ color: 'var(--st-miss)' }}>
          <AlertTriangle size={13} /> Could not be read
        </div>
        <div className="mt-1 line-clamp-2 text-[11px]" style={{ color: 'var(--text-3)' }}>
          {target.error}
        </div>
      </div>
    )
  }

  const segments: { status: ObjectStatus; count: number }[] = [
    { status: 'same', count: target.same },
    { status: 'formattingOnly', count: target.formattingOnly },
    { status: 'different', count: target.different },
    { status: 'missingInTarget', count: target.missingInTarget },
    { status: 'onlyInTarget', count: target.onlyInTarget },
    { status: 'unavailable', count: target.unavailable },
  ]
  const total = segments.reduce((n, s) => n + s.count, 0) || 1

  return (
    <div
      className="min-w-60 shrink-0 rounded-lg border px-3.5 py-2.5 transition-colors"
      style={{
        borderColor: colour?.line ?? 'var(--line)',
        background: colour?.tint ?? 'var(--surface-2)',
        borderLeftWidth: colour ? 3 : undefined,
        borderLeftColor: colour?.ink,
      }}
    >
      <button onClick={onFocus} className="block w-full text-left" title="Show only this target's differences">
        <div className="truncate text-[12.5px] font-semibold">{target.label}</div>
        <div className="truncate text-[10.5px]" style={{ color: 'var(--text-3)' }}>
          {target.server} / {target.database}
        </div>

        <div className="mt-1.5 flex items-baseline gap-1.5">
          <span className="text-[26px] font-semibold leading-none tabular-nums">
            {target.matchPercent.toFixed(1)}
            <span className="text-[15px]">%</span>
          </span>
          <span className="text-[11px]" style={{ color: 'var(--text-3)' }}>
            matches source
          </span>
        </div>

        {/* stacked meter - 2px surface gap between segments */}
        <div className="mt-2 flex h-1.5 gap-[2px] overflow-hidden rounded-full">
          {segments
            .filter((s) => s.count > 0)
            .map((s) => (
              <span
                key={s.status}
                style={{
                  width: `${(s.count / total) * 100}%`,
                  background: STATUS[s.status].colour,
                  borderRadius: 3,
                }}
                title={`${STATUS[s.status].label}: ${s.count}`}
              />
            ))}
        </div>
      </button>

      <div className="mt-2 flex flex-wrap gap-x-2.5 gap-y-0.5 text-[11px]">
        {segments
          .filter((s) => s.count > 0 && s.status !== 'same')
          .map((s) => (
            <button
              key={s.status}
              onClick={() => onFilterStatus(s.status)}
              className="inline-flex items-center gap-1 rounded hover:underline"
              style={{ color: 'var(--text-2)' }}
              title={STATUS[s.status].description}
            >
              <span
                aria-hidden
                className="inline-block h-2 w-2 rounded-full"
                style={{ background: STATUS[s.status].colour }}
              />
              <span className="tabular-nums">{s.count.toLocaleString()}</span>
              <span>{STATUS[s.status].label.toLowerCase()}</span>
            </button>
          ))}
      </div>
    </div>
  )
}

function TypeBreakdown({ result }: { result: CompareResult }) {
  const [expanded, setExpanded] = useState(false)
  const rows = expanded ? result.byType : result.byType.slice(0, 6)
  const max = Math.max(1, ...result.byType.map((b) => b.total))

  return (
    <div
      className="min-w-64 shrink-0 rounded-lg border px-3.5 py-2.5"
      style={{ borderColor: 'var(--line)', background: 'var(--surface-2)' }}
    >
      <div className="mb-1.5 flex items-center justify-between">
        <span className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
          Objects by type
        </span>
        {result.byType.length > 6 && (
          <button
            onClick={() => setExpanded((v) => !v)}
            className="text-[11px] hover:underline"
            style={{ color: 'var(--text-3)' }}
          >
            {expanded ? 'less' : `+${result.byType.length - 6}`}
          </button>
        )}
      </div>

      <div className="flex flex-col gap-1">
        {rows.map((b) => (
          <div key={b.type} className="flex items-center gap-2 text-[11.5px]">
            <span className="w-24 shrink-0 truncate" style={{ color: 'var(--text-2)' }}>
              {b.label}
            </span>
            <span className="relative h-2 flex-1 overflow-hidden rounded-sm" style={{ background: 'var(--raised)' }}>
              <span
                className="absolute inset-y-0 left-0 rounded-sm"
                style={{ width: `${(b.total / max) * 100}%`, background: 'var(--line)' }}
              />
              <span
                className="absolute inset-y-0 left-0 rounded-sm"
                style={{
                  width: `${(b.withDifferences / max) * 100}%`,
                  background: b.withDifferences > 0 ? 'var(--st-diff)' : 'transparent',
                }}
              />
            </span>
            <span className="w-16 shrink-0 text-right tabular-nums" style={{ color: 'var(--text-3)' }}>
              {b.withDifferences}/{b.total}
            </span>
          </div>
        ))}
      </div>

      <div className="mt-1.5 text-[10.5px]" style={{ color: 'var(--text-3)' }}>
        Amber = objects with at least one difference
      </div>
    </div>
  )
}
