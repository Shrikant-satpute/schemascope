import * as monaco from 'monaco-editor/editor/editor.api'
import { loader } from '@monaco-editor/react'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker'

// Only the SQL grammar is registered. Pulling in monaco's full entry point
// drags in a hundred other languages and a 7 MB TypeScript worker for no
// reason - SchemaScope only ever shows T-SQL.
import 'monaco-editor/languages/definitions/sql/register.js'

declare global {
  interface Window {
    MonacoEnvironment?: { getWorker: (workerId: string, label: string) => Worker }
  }
}

/**
 * Wires Monaco up to run entirely from the bundle.
 *
 * Nothing is fetched from a CDN: SchemaScope has to work on a locked down
 * machine with no internet, and it must never make an outbound request while
 * someone is looking at their database schema.
 */
export function createHighlighterCore() {
  window.MonacoEnvironment = {
    getWorker: () => new EditorWorker(),
  }
  loader.config({ monaco })
}

export { monaco }
