import { useEffect, useState } from 'react'
import { AlertTriangle, Clock, Eye, Moon, Sun } from 'lucide-react'
import { api } from './api'
import type { SharedComparison } from './types'
import { ResultsWorkspace } from './components/ResultsWorkspace'
import { Button, EmptyState, LogoMark, Spinner } from './components/ui'

const THEME_KEY = 'schemascope.theme'

function initialTheme(): 'dark' | 'light' {
  try {
    return localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light'
  } catch {
    return 'light'
  }
}

/**
 * What someone sees when they open a shared link.
 *
 * The same dashboard, filters, search and diff as the app itself - it renders
 * the very same workspace component - with everything they cannot use taken
 * away: no setup bar, no connections, no history, no export. There is nothing to
 * hide here, because the listener serving this page has no route to any of it.
 */
export function ViewerApp({ token }: { token: string }) {
  const [theme, setTheme] = useState<'dark' | 'light'>(initialTheme)
  const [shared, setShared] = useState<SharedComparison | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme)
    try {
      localStorage.setItem(THEME_KEY, theme)
    } catch {
      /* private mode */
    }
  }, [theme])

  useEffect(() => {
    void (async () => {
      try {
        setShared(await api.view.load(token))
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e))
      }
    })()
  }, [token])

  // Held open for as long as this page is. It is what puts this machine in the
  // owner's Share tab, and what takes it out again when the tab is closed.
  useEffect(() => {
    if (!shared) return
    return api.view.live(token)
  }, [shared, token])

  if (error) {
    return (
      <div className="h-full" style={{ background: 'var(--surface)' }}>
        <EmptyState
          icon={<AlertTriangle size={30} />}
          title="This link does not work"
          body="It may have been stopped by whoever shared it, or it may have run out of time. Ask them for a new one."
        />
      </div>
    )
  }

  if (!shared) {
    return (
      <div
        className="flex h-full items-center justify-center gap-2 text-[13px]"
        style={{ background: 'var(--page)', color: 'var(--text-2)' }}
      >
        <Spinner /> Loading the comparison...
      </div>
    )
  }

  const targets = shared.result.targets.map((t) => t.label).join(', ')

  return (
    <div className="flex h-full flex-col">
      <header
        className="flex shrink-0 flex-wrap items-center gap-3 border-b px-4 py-2"
        style={{ borderColor: 'var(--line)', background: 'var(--surface)' }}
      >
        <div className="flex items-center gap-2">
          <LogoMark />
          <span className="text-[14px] font-semibold tracking-tight">SchemaScope</span>
        </div>

        <span
          className="inline-flex items-center gap-1.5 rounded px-2 py-1 text-[11.5px] font-medium"
          style={{ background: 'var(--accent-soft)', color: 'var(--accent)' }}
        >
          <Eye size={12} /> Shared view - read only
        </span>

        <span className="min-w-0 truncate text-[12.5px]" style={{ color: 'var(--text-2)' }}>
          <b style={{ color: 'var(--text)' }}>{shared.sourceLabel}</b> against {targets}
        </span>

        <div className="flex-1" />

        <span
          className="inline-flex items-center gap-1.5 text-[11.5px]"
          style={{ color: 'var(--text-3)' }}
          title="When this comparison was run. It is a snapshot, not a live view."
        >
          <Clock size={12} />
          Compared {new Date(shared.comparedUtc).toLocaleString()} on {shared.hostName}
        </span>

        <Button size="sm" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label="Toggle theme">
          {theme === 'dark' ? <Sun size={14} /> : <Moon size={14} />}
        </Button>
      </header>

      <div className="flex min-h-0 flex-1 flex-col">
        <ResultsWorkspace
          result={shared.result}
          theme={theme}
          loadObject={(key) => api.view.object(token, key)}
          findReferences={(name) => api.view.references(token, name)}
          findUses={(key) => api.view.uses(token, key)}
        />
      </div>
    </div>
  )
}
