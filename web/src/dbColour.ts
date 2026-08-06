/**
 * A soft identity colour per database.
 *
 * The same server and database always gets the same tint everywhere it shows up
 * - the picker, the setup bar, its dashboard tile, its column in the matrix and
 * its tab in the diff header - so you can tell at a glance which database a
 * number belongs to without reading the label.
 *
 * The tints are alpha washes over whatever sits underneath, so row hover and the
 * selection highlight still show through them. Saturation and lightness live in
 * CSS variables that flip with the theme, so one definition covers light and
 * dark. Colour is never the only cue: every label is still written out in words.
 */

/** Twelve hues, spaced so neighbours stay distinguishable at this low chroma. */
const HUES = [214, 168, 140, 84, 44, 22, 354, 328, 300, 268, 240, 192]

export interface DbColour {
  /** Soft background wash - rows, cells, chips. */
  tint: string
  /** The same wash a touch stronger - headers and the selected row. */
  tintStrong: string
  /** Border or rule in the same hue. */
  line: string
  /** Readable text or icon colour in the same hue. */
  ink: string
}

const cache = new Map<string, DbColour>()

/** FNV-1a: small, stable, and spreads short strings evenly enough over 12 buckets. */
function hash(text: string): number {
  let h = 0x811c9dc5
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 0x01000193)
  }
  return h >>> 0
}

export function dbColour(key: string): DbColour {
  const hit = cache.get(key)
  if (hit) return hit

  const hue = HUES[hash(key) % HUES.length]
  const wash = (alpha: string) => `hsl(${hue} var(--db-sat) var(--db-light) / ${alpha})`

  const colour: DbColour = {
    tint: wash('var(--db-tint)'),
    tintStrong: wash('var(--db-tint-strong)'),
    line: wash('var(--db-edge)'),
    ink: `hsl(${hue} var(--db-ink-sat) var(--db-ink-light))`,
  }
  cache.set(key, colour)
  return colour
}

/** What makes a database that database: the server it lives on and its name. */
export function dbKey(server?: string | null, database?: string | null): string {
  return `${(server ?? '').trim().toLowerCase()}/${(database ?? '').trim().toLowerCase()}`
}

interface ConnectionLike {
  server?: string
  database?: string
  useRawConnectionString?: boolean
  rawConnectionString?: string | null
}

/** The key for a saved connection, optionally pointed at another database on the same server. */
export function connectionKey(c: ConnectionLike, database?: string): string {
  const db = database || c.database
  return c.useRawConnectionString ? dbKey(c.rawConnectionString ?? '', db) : dbKey(c.server, db)
}

export function connectionColour(c: ConnectionLike, database?: string): DbColour {
  return dbColour(connectionKey(c, database))
}
