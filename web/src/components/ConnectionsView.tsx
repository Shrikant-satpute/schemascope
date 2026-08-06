import { useState } from 'react'
import { Pencil, Plus, Server, Trash2, Lock, ShieldCheck } from 'lucide-react'
import type { ConnectionSettings } from '../types'
import { connectionColour } from '../dbColour'
import { api } from '../api'
import { Button, EmptyState, Panel } from './ui'
import { ConnectionDialog } from './ConnectionDialog'

const AUTH_SHORT: Record<string, string> = {
  windows: 'Windows auth',
  sqlLogin: 'SQL login',
  azureAdPassword: 'Entra password',
  azureAdInteractive: 'Entra interactive',
  azureAdDefault: 'Entra default',
}

export function ConnectionsView({
  connections,
  onChanged,
  secretsProtected,
}: {
  connections: ConnectionSettings[]
  onChanged: () => void
  secretsProtected: boolean
}) {
  const [editing, setEditing] = useState<ConnectionSettings | null>(null)
  const [adding, setAdding] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)

  async function remove(id: string) {
    await api.connections.remove(id)
    setConfirmDelete(null)
    onChanged()
  }

  return (
    <div className="flex h-full flex-col overflow-y-auto px-6 py-5">
      <div className="mx-auto w-full max-w-4xl">
        <div className="mb-5 flex items-end justify-between gap-4">
          <div>
            <h1 className="text-[19px] font-semibold">Connections</h1>
            <p className="mt-0.5 text-[12.5px]" style={{ color: 'var(--text-2)' }}>
              Add every database you want to compare. Add a server once, then point at different
              databases on it when you set up a compare.
            </p>
          </div>
          <Button variant="primary" onClick={() => setAdding(true)}>
            <Plus size={15} /> Add connection
          </Button>
        </div>

        <div
          className="mb-5 flex items-start gap-2.5 rounded-lg border px-3.5 py-2.5 text-[12.5px]"
          style={{ borderColor: 'var(--line)', background: 'var(--surface)', color: 'var(--text-2)' }}
        >
          <Lock size={15} className="mt-px shrink-0" style={{ color: 'var(--st-same)' }} />
          <div>
            <b style={{ color: 'var(--text)' }}>Everything stays on this PC.</b>{' '}
            {secretsProtected
              ? 'Passwords are encrypted with Windows DPAPI under your account. No other Windows user can read them, and nothing is ever sent anywhere.'
              : 'Password encryption is unavailable on this platform, so passwords are not saved. You will be asked each time.'}{' '}
            SchemaScope only ever reads - it cannot modify a database.
          </div>
        </div>

        {connections.length === 0 ? (
          <Panel className="h-72">
            <EmptyState
              icon={<Server size={30} />}
              title="No connections yet"
              body="Add your source database first - the one you treat as the truth - then add the databases you want to check against it."
              action={
                <Button variant="primary" onClick={() => setAdding(true)}>
                  <Plus size={15} /> Add your first connection
                </Button>
              }
            />
          </Panel>
        ) : (
          <div className="grid gap-2.5">
            {connections.map((c) => {
              const colour = connectionColour(c)
              return (
                <Panel
                  key={c.id}
                  className="flex items-center gap-4 px-4 py-3"
                  style={{
                    background: colour.tint,
                    borderColor: colour.line,
                    borderLeftWidth: 3,
                    borderLeftColor: colour.ink,
                  }}
                >
                  <div
                    className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md"
                    style={{ background: colour.tintStrong, color: colour.ink }}
                  >
                    <Server size={17} />
                  </div>
  
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span className="truncate text-[13.5px] font-medium">
                        {c.label || c.server || 'Unnamed'}
                      </span>
                      {c.protectedServer && (
                        <span
                          className="inline-flex items-center gap-1 rounded px-1.5 py-px text-[10.5px] font-medium"
                          style={{
                            background: 'color-mix(in srgb, var(--st-miss) 15%, transparent)',
                            color: 'var(--st-miss)',
                          }}
                        >
                          <ShieldCheck size={10} /> Protected
                        </span>
                      )}
                    </div>
                    <div className="mt-0.5 truncate font-mono text-[11.5px]" style={{ color: 'var(--text-2)' }}>
                      {c.useRawConnectionString
                        ? c.rawConnectionString || 'connection string'
                        : `${c.server}${c.database ? ` / ${c.database}` : ''}`}
                    </div>
                  </div>
  
                  <span className="shrink-0 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
                    {AUTH_SHORT[c.auth] ?? c.auth}
                  </span>
  
                  <div className="flex shrink-0 gap-1">
                    <Button size="sm" onClick={() => setEditing(c)} aria-label="Edit">
                      <Pencil size={14} />
                    </Button>
                    {confirmDelete === c.id ? (
                      <>
                        <Button size="sm" variant="danger" onClick={() => remove(c.id)}>
                          Delete
                        </Button>
                        <Button size="sm" onClick={() => setConfirmDelete(null)}>
                          Cancel
                        </Button>
                      </>
                    ) : (
                      <Button size="sm" variant="danger" onClick={() => setConfirmDelete(c.id)} aria-label="Delete">
                        <Trash2 size={14} />
                      </Button>
                    )}
                  </div>
                </Panel>
              )
            })}
          </div>
        )}
      </div>

      {(adding || editing) && (
        <ConnectionDialog
          initial={editing ?? undefined}
          onClose={() => {
            setAdding(false)
            setEditing(null)
          }}
          onSaved={() => {
            setAdding(false)
            setEditing(null)
            onChanged()
          }}
        />
      )}
    </div>
  )
}
