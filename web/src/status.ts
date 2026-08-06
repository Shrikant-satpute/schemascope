import type { ObjectStatus } from './types'

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
] as const
