import type { DatabaseEngine } from './types'

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

/** The port an engine listens on when a connection names none (SQL Server keeps its port in the server name). */
const DEFAULT_PORTS: Partial<Record<DatabaseEngine, number>> = { postgreSql: 5432, mySql: 3306, mariaDb: 3306 }

/**
 * What makes a database that database. SQL Server: the server it lives on and its name, letter case aside (keys as
 * they always were). Any other engine: engine|host|port|name - its port is apart from the host, so two servers on one
 * host get two keys, and its database name is exact ("App" and "app" are two databases).
 */
export function dbKey(server?: string | null, database?: string | null, engine?: DatabaseEngine | null, port?: number | null): string {
  if (!engine || engine === 'sqlServer') return `${(server ?? '').trim().toLowerCase()}/${(database ?? '').trim().toLowerCase()}`
  return `${engine}|${(server ?? '').trim().toLowerCase()}|${port ?? DEFAULT_PORTS[engine] ?? ''}|${(database ?? '').trim()}`
}

/** host:port for an engine that keeps the port apart (PostgreSQL); the server as it is for SQL Server. */
export function serverWithPort(server: string, port?: number | null): string {
  return port != null ? `${server}:${port}` : server
}

interface ConnectionLike {
  engine?: DatabaseEngine
  server?: string
  port?: number | null
  database?: string
  useRawConnectionString?: boolean
  rawConnectionString?: string | null
}

/** The key for a saved connection, optionally pointed at another database on the same server. */
export function connectionKey(c: ConnectionLike, database?: string): string {
  const db = database || c.database
  return c.useRawConnectionString ? dbKey(c.rawConnectionString ?? '', db, c.engine) : dbKey(c.server, db, c.engine, c.port)
}

export function connectionColour(c: ConnectionLike, database?: string): DbColour {
  return dbColour(connectionKey(c, database))
}
