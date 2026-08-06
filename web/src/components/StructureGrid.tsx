import type { StructureSection } from '../types'
import type { DbColour } from '../dbColour'

const STATUS_COLOUR: Record<string, string> = {
  same: 'var(--text)',
  changed: 'var(--st-diff)',
  missingInTarget: 'var(--st-miss)',
  onlyInTarget: 'var(--st-extra)',
}

const STATUS_SYMBOL: Record<string, string> = {
  same: '',
  changed: '≠',
  missingInTarget: '✕',
  onlyInTarget: '+',
}

const STATUS_WORD: Record<string, string> = {
  same: 'same',
  changed: 'changed',
  missingInTarget: 'missing in target',
  onlyInTarget: 'only in target',
}

/**
 * Field level view of a table.
 *
 * A CREATE TABLE text diff makes one renamed column light up the whole block.
 * This shows exactly which column and which property moved, and highlights only
 * the cells that actually differ.
 */
export function StructureGrid({
  sections,
  sourceLabel,
  targetLabel,
  sourceColour,
  targetColour,
  showUnchanged,
  onToggleUnchanged,
}: {
  sections: StructureSection[]
  sourceLabel: string
  targetLabel: string
  sourceColour?: DbColour | null
  targetColour?: DbColour | null
  showUnchanged: boolean
  onToggleUnchanged: (v: boolean) => void
}) {
  const totalChanged = sections.reduce((n, s) => n + s.changedCount, 0)

  // The two database columns carry their own tint, so which side you are
  // reading is obvious without tracking the header label.
  const sourceHead = sourceColour
    ? { background: sourceColour.tintStrong, color: sourceColour.ink }
    : undefined
  const targetHead = targetColour
    ? { background: targetColour.tintStrong, color: targetColour.ink }
    : undefined

  return (
    <div className="h-full overflow-auto px-4 py-3">
      <div className="mb-3 flex items-center gap-3">
        <span className="text-[12px]" style={{ color: 'var(--text-2)' }}>
          {totalChanged === 0 ? (
            <>No structural differences.</>
          ) : (
            <>
              <b style={{ color: 'var(--text)' }}>{totalChanged}</b> structural difference
              {totalChanged === 1 ? '' : 's'}
            </>
          )}
        </span>
        <label className="flex cursor-pointer items-center gap-1.5 text-[12px]" style={{ color: 'var(--text-2)' }}>
          <input
            type="checkbox"
            checked={showUnchanged}
            onChange={(e) => onToggleUnchanged(e.target.checked)}
          />
          Show unchanged rows
        </label>
      </div>

      {sections.map((section) => {
        const rows = showUnchanged ? section.rows : section.rows.filter((r) => r.status !== 'same')
        if (rows.length === 0) return null
        const isColumns = section.title === 'Columns'

        return (
          <div key={section.title} className="mb-5">
            <div className="mb-1.5 flex items-baseline gap-2">
              <h4 className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
                {section.title}
              </h4>
              <span className="text-[11px]" style={{ color: 'var(--text-3)' }}>
                {section.changedCount} of {section.rows.length} changed
              </span>
            </div>

            <div className="overflow-x-auto rounded-md border" style={{ borderColor: 'var(--line)' }}>
              <table className="w-full text-[12px]">
                <thead>
                  <tr style={{ background: 'var(--surface-2)', color: 'var(--text-3)' }}>
                    <th className="w-6 px-2 py-1.5" />
                    <th className="px-2 py-1.5 text-left font-medium">Name</th>
                    {isColumns ? (
                      <>
                        <th className="px-2 py-1.5 text-left font-medium" style={sourceHead}>
                          {sourceLabel} type
                        </th>
                        <th className="px-2 py-1.5 text-left font-medium" style={targetHead}>
                          {targetLabel} type
                        </th>
                        <th className="px-2 py-1.5 text-left font-medium">Null</th>
                        <th className="px-2 py-1.5 text-left font-medium">Default</th>
                      </>
                    ) : (
                      <>
                        <th className="px-2 py-1.5 text-left font-medium" style={sourceHead}>
                          {sourceLabel}
                        </th>
                        <th className="px-2 py-1.5 text-left font-medium" style={targetHead}>
                          {targetLabel}
                        </th>
                      </>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r) => {
                    const changed = new Set(r.changedFields)
                    const cellStyle = (field: string) =>
                      changed.has(field)
                        ? {
                            background: 'color-mix(in srgb, var(--st-diff) 16%, transparent)',
                            color: 'var(--st-diff)',
                          }
                        : undefined

                    return (
                      <tr key={r.name} className="border-t" style={{ borderColor: 'var(--line-soft)' }}>
                        <td
                          className="px-2 py-1 text-center font-mono font-semibold"
                          style={{ color: STATUS_COLOUR[r.status] }}
                          title={STATUS_WORD[r.status]}
                        >
                          {STATUS_SYMBOL[r.status]}
                        </td>
                        <td className="px-2 py-1 font-mono" style={{ color: STATUS_COLOUR[r.status] }}>
                          {r.name}
                        </td>

                        {isColumns ? (
                          <>
                            <td className="px-2 py-1 font-mono" style={cellStyle('type')}>
                              {r.left?.type ?? '-'}
                            </td>
                            <td className="px-2 py-1 font-mono" style={cellStyle('type')}>
                              {r.right?.type ?? '-'}
                            </td>
                            <td className="px-2 py-1 font-mono" style={cellStyle('nullable')}>
                              {r.left?.nullable ?? '-'}
                              {r.right && r.left?.nullable !== r.right.nullable ? ` → ${r.right.nullable}` : ''}
                            </td>
                            <td className="max-w-64 truncate px-2 py-1 font-mono" style={cellStyle('default')}>
                              {r.left?.default || r.right?.default || ''}
                            </td>
                          </>
                        ) : (
                          <>
                            <td className="max-w-96 px-2 py-1 font-mono break-all">{r.leftText ?? '-'}</td>
                            <td className="max-w-96 px-2 py-1 font-mono break-all">{r.rightText ?? '-'}</td>
                          </>
                        )}
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )
      })}
    </div>
  )
}
