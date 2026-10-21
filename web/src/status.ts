import type { CompareRow, ObjectStatus, TypeBreakdown } from './types'

/**
 * One place that decides how a status looks and reads.
 *
 * Every status carries a symbol AND a word as well as a colour, so the grid is
 * still readable with colour vision deficiency, in greyscale, or printed. The
 * colours themselves are validated in both light and dark mode.
 */
export interface StatusStyle {
  /** CSS variable holding the colour. */
  colour: string
  symbol: string
  label: string
  /** Longer sentence for tooltips and the legend. */
  description: string
  /** Sort weight - higher is more urgent. */
  severity: number
}

export const STATUS: Record<ObjectStatus, StatusStyle> = {
  same: {
    colour: 'var(--st-same)',
    symbol: '=',
    label: 'Same',
    description: 'Identical to the source',
    severity: 0,
  },
  formattingOnly: {
    colour: 'var(--st-fmt)',
    symbol: '~',
    label: 'Formatting',
    description: 'Same code, only the layout or comments moved',
    severity: 1,
  },
  unavailable: {
    colour: 'var(--st-na)',
    symbol: '?',
    label: 'Unreadable',
    description: 'Encrypted, or this login cannot read the body',
    severity: 2,
  },
  different: {
    colour: 'var(--st-diff)',
    symbol: '≠',
    label: 'Different',
    description: 'A real difference in the definition',
    severity: 3,
  },
  onlyInTarget: {
    colour: 'var(--st-extra)',
    symbol: '+',
    label: 'Extra',
    description: 'Exists here but not in the source - usually a local customisation',
    severity: 4,
  },
  missingInTarget: {
    colour: 'var(--st-miss)',
    symbol: '✕',
    label: 'Missing',
    description: 'In the source but not here',
    severity: 5,
  },
}

export const STATUS_ORDER: ObjectStatus[] = [
  'missingInTarget',
  'onlyInTarget',
  'different',
  'unavailable',
  'formattingOnly',
  'same',
]

export function statusOf(s: ObjectStatus): StatusStyle {
  return STATUS[s] ?? STATUS.same
}

/** Short cell text: the symbol, plus the changed line count when there is one. */
export function cellText(status: ObjectStatus, changedLines: number): string {
  const style = statusOf(status)
  if (status === 'different' && changedLines > 0) return `${style.symbol} ${changedLines}`
  return style.symbol
}

/** The non-identical statuses a type's count is split into, in bar order. */
export const TYPE_COUNT_ORDER = [
  'different',
  'missingInTarget',
  'onlyInTarget',
  'formattingOnly',
  'unavailable',
] as const

export type TypeCounts = Record<(typeof TYPE_COUNT_ORDER)[number], number>

/** How each type count reads in a tooltip or legend. */
export const TYPE_COUNT_WORD: Record<keyof TypeCounts, string> = {
  different: 'differ',
  missingInTarget: 'missing',
  onlyInTarget: 'extra',
  formattingOnly: 'formatting only',
  unavailable: 'unreadable',
}

/**
 * A type's non-identical rows split by each row's worst status, so a missing or
 * extra object is not called a difference. Snapshots saved before the server
 * sent the split carry none (or zeros), so it is rebuilt from the rows.
 */
export function typeCounts(b: TypeBreakdown, rows: CompareRow[]): TypeCounts {
  const counts = Object.fromEntries(TYPE_COUNT_ORDER.map((s) => [s, b[s] ?? 0])) as TypeCounts
  if (TYPE_COUNT_ORDER.reduce((n, s) => n + counts[s], 0) === b.withDifferences) return counts

  for (const s of TYPE_COUNT_ORDER) counts[s] = 0
  for (const r of rows) {
    if (r.type === b.type && r.worst !== 'same') counts[r.worst]++
  }
  return counts
}

export const TYPE_ORDER = [
  'schema',
  'table',
  'view',
  'storedProcedure',
  'scalarFunction',
  'tableValuedFunction',
  'aggregateFunction',
  'trigger',
  'tableType',
  'userDefinedType',
  'sequence',
  'synonym',
  'materializedView',
  'domain',
  'enumType',
  'compositeType',
  'extension',
] as const
