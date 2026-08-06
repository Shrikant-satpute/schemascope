import { useState } from 'react'
import { ArrowRight, ChevronDown, Play, Plus, Settings2, Square, X } from 'lucide-react'
import type { ConnectionSettings } from '../types'
import { connectionColour, type DbColour } from '../dbColour'
import { Button, Spinner } from './ui'
import { ConnectionPicker, type PickedConnection } from './ConnectionPicker'

export interface TargetSlot {
  connectionId: string
  database?: string
}

/**
 * Source on the left, many targets on the right. The whole point of the tool is
 * that the right hand side is a list, not a single box.
 *
 * Both sides open the same picker, so you can connect to something new without
 * first going off to the Connections screen.
 */
export function SetupBar({
  connections,
  source,
  targets,
  running,
  elapsedMs,
  onSourceChange,
  onTargetsChange,
  onRun,
  onCancel,
  onOpenOptions,
  optionCount,
  onConnectionsChanged,
}: {
  connections: ConnectionSettings[]
  source: TargetSlot | null
  targets: TargetSlot[]
  running: boolean
  elapsedMs: number | null
  onSourceChange: (s: TargetSlot | null) => void
  onTargetsChange: (t: TargetSlot[]) => void
  onRun: () => void
  onCancel: () => void
  onOpenOptions: () => void
  optionCount: number
  onConnectionsChanged: () => void
}) {
  const [picking, setPicking] = useState<'source' | 'target' | null>(null)

  const byId = new Map(connections.map((c) => [c.id, c]))

  const describe = (slot: TargetSlot) => {
    const c = byId.get(slot.connectionId)
    if (!c) return { name: 'unknown', detail: '', colour: null }
    const db = slot.database || c.database
    return {
      name: c.label || c.server,
      detail: `${c.server}${db ? ` / ${db}` : ''}`,
      colour: connectionColour(c, slot.database),
    }
  }

  function handlePick(picked: PickedConnection) {
    if (picking === 'source') onSourceChange(picked)
    else onTargetsChange([...targets, picked])
    setPicking(null)
  }

  // A database should not be compared against itself, so hide what is already
  // in play on the other side of the arrow.
  const excludeFor = (which: 'source' | 'target') =>
    which === 'source'
      ? targets.map((t) => t.connectionId)
      : [...(source ? [source.connectionId] : []), ...targets.map((t) => t.connectionId)]

  return (
    <>
      <div
        className="flex flex-wrap items-center gap-2 border-b px-4 py-2.5"
        style={{ borderColor: 'var(--line)', background: 'var(--surface)' }}
      >
        <span
          className="text-[11px] font-medium uppercase tracking-wide"
          style={{ color: 'var(--text-3)' }}
        >
          Source
        </span>

        <SlotButton
          slot={source}
          describe={describe}
          placeholder="Select source..."
          disabled={running}
          onClick={() => setPicking('source')}
          onClear={() => onSourceChange(null)}
        />

        <ArrowRight size={16} style={{ color: 'var(--text-3)' }} />

        <span
          className="text-[11px] font-medium uppercase tracking-wide"
          style={{ color: 'var(--text-3)' }}
        >
          Compare against
        </span>

        <div className="flex flex-wrap items-center gap-1.5">
          {targets.map((t, i) => {
            const { name, detail, colour } = describe(t)
            return (
              <span
                key={`${t.connectionId}-${t.database ?? ''}-${i}`}
                className="inline-flex items-center gap-1.5 rounded-md border py-1 pl-2.5 pr-1 text-[12.5px]"
                style={{
                  borderColor: colour?.line ?? 'var(--line)',
                  background: colour?.tint ?? 'var(--raised)',
                }}
                title={detail}
              >
                {name}
                {t.database && (
                  <span className="font-mono text-[11px]" style={{ color: 'var(--text-3)' }}>
                    {t.database}
                  </span>
                )}
                <button
                  onClick={() => onTargetsChange(targets.filter((_, j) => j !== i))}
                  disabled={running}
                  className="rounded p-0.5 transition-colors hover:bg-[var(--line)] disabled:opacity-40"
                  aria-label={`Remove ${name}`}
                >
                  <X size={12} />
                </button>
              </span>
            )
          })}

          <Button variant="outline" onClick={() => setPicking('target')} disabled={running}>
            <Plus size={14} /> Add target
          </Button>
        </div>

        <div className="flex-1" />

        {elapsedMs !== null && !running && (
          <span className="font-mono text-[12px]" style={{ color: 'var(--text-3)' }}>
            {elapsedMs} ms
          </span>
        )}

        <Button variant="outline" onClick={onOpenOptions} disabled={running}>
          <Settings2 size={14} /> Options
          {optionCount > 0 && (
            <span
              className="ml-0.5 rounded px-1 text-[10.5px] font-semibold"
              style={{ background: 'var(--accent-soft)', color: 'var(--accent)' }}
            >
              {optionCount}
            </span>
          )}
        </Button>

        {running ? (
          <Button variant="outline" onClick={onCancel}>
            <Square size={13} /> Stop
          </Button>
        ) : (
          <Button variant="primary" onClick={onRun} disabled={!source || targets.length === 0}>
            <Play size={14} /> Compare
          </Button>
        )}

        {running && <Spinner />}
      </div>

      {picking && (
        <ConnectionPicker
          title={picking === 'source' ? 'Select source database' : 'Add a database to compare against'}
          connections={connections}
          exclude={excludeFor(picking)}
          onClose={() => setPicking(null)}
          onPick={handlePick}
          onConnectionsChanged={onConnectionsChanged}
        />
      )}
    </>
  )
}

function SlotButton({
  slot,
  describe,
  placeholder,
  disabled,
  onClick,
  onClear,
}: {
  slot: TargetSlot | null
  describe: (s: TargetSlot) => { name: string; detail: string; colour: DbColour | null }
  placeholder: string
  disabled?: boolean
  onClick: () => void
  onClear: () => void
}) {
  const info = slot ? describe(slot) : null

  return (
    <span className="inline-flex items-center">
      <button
        onClick={onClick}
        disabled={disabled}
        className="inline-flex min-w-56 items-center gap-2 rounded-md border px-2.5 py-1.5 text-left text-[12.5px] transition-colors disabled:opacity-40"
        style={{
          borderColor: info?.colour?.line ?? 'var(--line)',
          background: info?.colour?.tint ?? 'var(--page)',
          borderLeft: info?.colour ? `3px solid ${info.colour.ink}` : undefined,
        }}
        title={info?.detail}
      >
        {info ? (
          <span className="min-w-0 flex-1">
            <span className="block truncate">{info.name}</span>
            <span className="block truncate font-mono text-[10.5px]" style={{ color: 'var(--text-3)' }}>
              {info.detail}
            </span>
          </span>
        ) : (
          <span className="flex-1" style={{ color: 'var(--text-3)' }}>
            {placeholder}
          </span>
        )}
        <ChevronDown size={14} style={{ color: 'var(--text-3)' }} />
      </button>

      {info && !disabled && (
        <button
          onClick={onClear}
          className="ml-1 rounded p-1 transition-colors hover:bg-[var(--raised)]"
          style={{ color: 'var(--text-3)' }}
          aria-label="Clear source"
        >
          <X size={12} />
        </button>
      )}
    </span>
  )
}
