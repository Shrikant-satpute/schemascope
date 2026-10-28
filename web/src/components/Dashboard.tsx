import { AlertTriangle, ChevronDown, ChevronUp, GitCompareArrows, Clock, FileCode } from 'lucide-react'
import { useState } from 'react'
import clsx from 'clsx'
import type { CompareResult, ObjectStatus } from '../types'
import type { DbColour } from '../dbColour'
import { STATUS, TYPE_COUNT_ORDER, TYPE_COUNT_WORD, typeCounts } from '../status'
import { engineChip } from '../engines'
import { serverWithPort } from '../dbColour'
import { EngineChip } from './ui'

/** Names for the stage keys the engine reports, in the order the work happens. */
const STAGES: [key: string, label: string][] = [
  ['read', 'reading the databases'],
  ['index', 'indexing'],
  ['compare', 'comparing'],
  ['summarise', 'summarising'],
]

/**
 * The per-stage split, for the tooltip on the elapsed time.
 *
 * A slow run is almost always a slow read, and a slow read is almost always the
 * server rather than SchemaScope - so the number that answers "why" is worth
 * having in reach instead of buried in the payload.
 */
function stageBreakdown(result: CompareResult): string {
  const timings = result.timings
  if (!timings) return `${result.totalMs} ms in total`

  const parts = STAGES.filter(([key]) => timings[key] !== undefined).map(
    ([key, label]) => `${label}: ${timings[key]} ms`,
  )

  parts.push(`total: ${result.totalMs} ms`)
  return parts.join('\n')
}

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
  onCreateScripts,
}: {
  result: CompareResult
  sourceColour: DbColour | null
  targetColours: DbColour[]
  onFocusTarget: (index: number) => void
  onFilterStatus: (index: number, status: ObjectStatus) => void
  collapsed: boolean
  onToggle: () => void
  /**
   * Owner only. Takes the index into `result.targets`, not the tile's place
   * on screen (the tiles are sorted worst first). Shared links leave it out,
   * so the button is never drawn for a viewer.
   */
  onCreateScripts?: (index: number) => void
}) {
  // engine chips (with the version) once a run is not SQL Server; a SQL Server run looks as it always has
  const showEngine = [result.source.engine, ...result.targets.map((t) => t.engine)].some((e) => e && e !== 'sqlServer')
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
          <span
            className="inline-flex items-center gap-1"
            title={stageBreakdown(result)}
          >
            <Clock size={11} />
            {result.totalMs} ms
            {/* Reading the databases is nearly always the whole cost. Saying so
                here turns "why was that slow" into something you can see. */}
            {result.timings?.read !== undefined && result.totalMs > 0 && (
              <span style={{ color: 'var(--text-3)' }}>
                ({Math.round((result.timings.read / result.totalMs) * 100)}% reading)
              </span>
            )}
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
              onCreateScripts={onCreateScripts}
              showEngine={showEngine}
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

/**
 * One target's headline. The "Create scripts" button sits in the top-right
 * corner as a sibling of the focus button, never inside it - a button inside a
 * button is invalid HTML and the click would also focus the target. A tile that
 * could not be read gets no button: there is nothing to script against.
 */
function TargetTile({
  index,
  target,
  colour,
  onFocus,
  onFilterStatus,
  onCreateScripts,
  showEngine,
}: {
  index: number
  target: CompareResult['targets'][number]
  colour: DbColour | null
  onFocus: () => void
  onFilterStatus: (s: ObjectStatus) => void
  onCreateScripts?: (index: number) => void
  showEngine?: boolean
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
      className="relative min-w-60 shrink-0 rounded-lg border px-3.5 py-2.5 transition-colors"
      style={{
        borderColor: colour?.line ?? 'var(--line)',
        background: colour?.tint ?? 'var(--surface-2)',
        borderLeftWidth: colour ? 3 : undefined,
        borderLeftColor: colour?.ink,
      }}
    >
      {onCreateScripts && (
        <button
          onClick={() => onCreateScripts(index)}
          className="absolute right-2 top-2 rounded p-1 transition-colors hover:bg-[var(--raised)]"
          style={{ color: 'var(--text-3)' }}
          title="Create scripts that make this target look like the source"
          aria-label={`Create scripts for ${target.label}`}
        >
          <FileCode size={13} />
        </button>
      )}

      <button onClick={onFocus} className="block w-full text-left" title="Show only this target's differences">
        <div className={clsx('truncate text-[12.5px] font-semibold', onCreateScripts && 'pr-6')}>{target.label}</div>
        <div
          className={clsx('flex items-center gap-1.5 truncate text-[10.5px]', onCreateScripts && 'pr-6')}
          style={{ color: 'var(--text-3)' }}
        >
          {showEngine && <EngineChip label={engineChip(target.engine, target.productVersion)} />}
          <span className="truncate">
            {serverWithPort(target.server, target.port)} / {target.database}
          </span>
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

/** Words for the per-type split - "differ" means a changed definition only. */
function TypeBreakdown({ result }: { result: CompareResult }) {
  const [expanded, setExpanded] = useState(false)
  const rows = expanded ? result.byType : result.byType.slice(0, 6)
  const max = Math.max(1, ...result.byType.map((b) => b.total))
  // '?' only earns a legend entry when some type has an unreadable object
  const anyUnavailable = result.byType.some((b) => typeCounts(b, result.rows).unavailable > 0)

  return (
    <div
      className="min-w-80 shrink-0 rounded-lg border px-3.5 py-2.5"
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
        {rows.map((b) => {
          const counts = typeCounts(b, result.rows)
          const shown = TYPE_COUNT_ORDER.filter((s) => counts[s] > 0)
          const title =
            `${b.label}: ${b.total} object(s)\n` +
            TYPE_COUNT_ORDER.map((s) => `${counts[s]} ${TYPE_COUNT_WORD[s]}`).join(' · ')
          return (
            <div key={b.type} className="flex items-center gap-2 text-[11.5px]" title={title}>
              <span className="w-24 shrink-0 truncate" style={{ color: 'var(--text-2)' }}>
                {b.label}
              </span>
              <span className="relative flex h-2 flex-1 overflow-hidden rounded-sm" style={{ background: 'var(--raised)' }}>
                <span
                  className="absolute inset-y-0 left-0 rounded-sm"
                  style={{ width: `${(b.total / max) * 100}%`, background: 'var(--line)' }}
                />
                {shown.map((s) => (
                  <span
                    key={s}
                    className="relative h-full"
                    style={{ width: `${(counts[s] / max) * 100}%`, background: STATUS[s].colour }}
                  />
                ))}
              </span>
              <span className="min-w-28 shrink-0 whitespace-nowrap text-right tabular-nums" style={{ color: 'var(--text-3)' }}>
                {shown.length === 0
                  ? 0
                  : shown.map((s) => (
                      <span key={s} className="ml-1" style={{ color: STATUS[s].colour }}>
                        {STATUS[s].symbol}
                        {counts[s]}
                      </span>
                    ))}
                <span className="ml-1">/ {b.total}</span>
              </span>
            </div>
          )
        })}
      </div>

      <div className="mt-1.5 flex flex-wrap gap-x-2 text-[10.5px]" style={{ color: 'var(--text-3)' }}>
        {TYPE_COUNT_ORDER.filter((s) => s !== 'unavailable' || anyUnavailable).map((s) => (
          <span key={s}>
            <span className="font-mono font-semibold" style={{ color: STATUS[s].colour }}>
              {STATUS[s].symbol}
            </span>{' '}
            {TYPE_COUNT_WORD[s]}
          </span>
        ))}
      </div>
    </div>
  )
}
