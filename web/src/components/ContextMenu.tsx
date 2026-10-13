import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'

export interface MenuItem {
  label: string
  /** Shortcut or explanation, shown greyed on the right. */
  hint?: string
  icon?: ReactNode
  onSelect: () => void
  disabled?: boolean
}

export type MenuEntry = MenuItem | 'separator'

export interface MenuState {
  x: number
  y: number
  entries: MenuEntry[]
}

/**
 * One right-click menu for the whole app.
 *
 * The diff editor, the matrix and the table grid are three very different things -
 * a Monaco editor, a virtualized list and an HTML table - so a single component
 * they all raise beats three menus that drift apart.
 */
export function ContextMenu({ state, onClose }: { state: MenuState | null; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const [position, setPosition] = useState({ x: 0, y: 0 })

  // Open near the pointer, but never off the edge of the window.
  useLayoutEffect(() => {
    if (!state || !ref.current) return

    const box = ref.current.getBoundingClientRect()
    setPosition({
      x: Math.max(4, Math.min(state.x, window.innerWidth - box.width - 4)),
      y: Math.max(4, Math.min(state.y, window.innerHeight - box.height - 4)),
    })
  }, [state])

  useEffect(() => {
    if (!state) return

    const close = () => onClose()
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()

    window.addEventListener('keydown', onKey)
    window.addEventListener('resize', close)

    // Scrolling moves the page out from under the menu, so it should go. But the
    // virtualized grid settles its own scroll position just after the click, and
    // that must not count - it would shut the menu before it could be read.
    const armScroll = window.setTimeout(() => window.addEventListener('scroll', close, true), 250)

    return () => {
      window.clearTimeout(armScroll)
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', close)
      window.removeEventListener('scroll', close, true)
    }
  }, [state, onClose])

  if (!state) return null

  return (
    <div
      className="fixed inset-0 z-50"
      onMouseDown={onClose}
      onContextMenu={(e) => {
        e.preventDefault()
        onClose()
      }}
    >
      <div
        ref={ref}
        className="ss-fade absolute min-w-52 rounded-md border py-1 shadow-xl"
        style={{
          left: position.x,
          top: position.y,
          background: 'var(--surface)',
          borderColor: 'var(--line)',
        }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {state.entries.map((entry, i) =>
          entry === 'separator' ? (
            <div key={i} className="my-1 border-t" style={{ borderColor: 'var(--line-soft)' }} />
          ) : (
            <button
              key={i}
              disabled={entry.disabled}
              onClick={() => {
                onClose()
                entry.onSelect()
              }}
              className="flex w-full items-center gap-2 px-3 py-1 text-left text-[12.5px] transition-colors hover:bg-[var(--raised)] disabled:cursor-not-allowed disabled:opacity-40"
            >
              {entry.icon && <span className="shrink-0" style={{ color: 'var(--text-3)' }}>{entry.icon}</span>}
              <span className="flex-1 truncate">{entry.label}</span>
              {entry.hint && (
                <span className="shrink-0 text-[11px]" style={{ color: 'var(--text-3)' }}>
                  {entry.hint}
                </span>
              )}
            </button>
          ),
        )}
      </div>
    </div>
  )
}
