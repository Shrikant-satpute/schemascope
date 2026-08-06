import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Check, Database, Plus, Search, Server, History, Trash2 } from 'lucide-react'
import { api } from '../api'
import type { ConnectionSettings, DatabaseListItem } from '../types'
import { connectionColour } from '../dbColour'
import { Button, Input, Modal, Spinner } from './ui'
import { ConnectionForm, BLANK_CONNECTION, AUTH_LABELS } from './ConnectionForm'

export interface PickedConnection {
  connectionId: string
  database?: string
}

/**
 * Opens from "Select source" and "Add target".
 *
 * Recent picks from what you already have; New connects to something fresh and
 * saves it on the way through. Either way you never have to leave the compare
 * screen to get connected.
 */
export function ConnectionPicker({
  title,
  connections,
  exclude,
  onClose,
  onPick,
  onConnectionsChanged,
}: {
  title: string
  connections: ConnectionSettings[]
  exclude?: string[]
  onClose: () => void
  onPick: (picked: PickedConnection) => void
  onConnectionsChanged: () => void
}) {
  const [tab, setTab] = useState<'recent' | 'new'>(connections.length > 0 ? 'recent' : 'new')
  const [search, setSearch] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [database, setDatabase] = useState<string>('')
  const [databases, setDatabases] = useState<DatabaseListItem[] | null>(null)
  const [loadingDbs, setLoadingDbs] = useState(false)

  const [form, setForm] = useState<Partial<ConnectionSettings>>(BLANK_CONNECTION)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)

  const excluded = new Set(exclude ?? [])

  // Most recently used first - the one you want is almost always at the top.
  const recent = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return connections
      .filter((c) => !excluded.has(c.id))
      .filter((c) =>
        needle
          ? `${c.label} ${c.server} ${c.database}`.toLowerCase().includes(needle)
          : true,
      )
      .sort((a, b) => {
        const at = a.lastUsedUtc ? Date.parse(a.lastUsedUtc) : 0
        const bt = b.lastUsedUtc ? Date.parse(b.lastUsedUtc) : 0
        return bt - at
      })
  }, [connections, search, exclude])

  const selected = connections.find((c) => c.id === selectedId) ?? null
  const selectedColour = selected ? connectionColour(selected, database) : null

  // Offer the other databases on that server, so one saved server can be used
  // many times over without saving it again.
  useEffect(() => {
    if (!selected) {
      setDatabases(null)
      setDatabase('')
      return
    }
    setDatabase(selected.database ?? '')
    setDatabases(null)

    let cancelled = false
    setLoadingDbs(true)
    api.connections
      .databases(selected)
      .then((list) => {
        if (!cancelled) setDatabases(list)
      })
      .catch(() => {
        if (!cancelled) setDatabases(null)
      })
      .finally(() => {
        if (!cancelled) setLoadingDbs(false)
      })

    return () => {
      cancelled = true
    }
  }, [selectedId])

  function confirmRecent() {
    if (!selected) return
    onPick({
      connectionId: selected.id,
      database: database && database !== selected.database ? database : undefined,
    })
  }

  // Removing a saved connection here saves a trip to the Connections screen just
  // to tidy up a list that has grown. It only forgets how to connect - nothing
  // is touched on the server.
  async function remove(id: string) {
    setDeleting(true)
    setDeleteError(null)
    try {
      await api.connections.remove(id)
      if (selectedId === id) setSelectedId(null)
      setConfirmDelete(null)
      onConnectionsChanged()
    } catch (e) {
      setDeleteError(e instanceof Error ? e.message : String(e))
    } finally {
      setDeleting(false)
    }
  }

  async function saveAndPick() {
    if (!form.server && !form.useRawConnectionString) {
      setSaveError('Enter a server name.')
      return
    }
    setSaving(true)
    setSaveError(null)
    try {
      const saved = await api.connections.save(form)
      onConnectionsChanged()
      onPick({ connectionId: saved.id })
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
      setSaving(false)
    }
  }

  return (
    <Modal
      title={title}
      subtitle="Pick a database you have used before, or connect to a new one. The bin icon forgets one you no longer need."
      onClose={onClose}
      width={700}
      footer={
        tab === 'recent' ? (
          <>
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary" onClick={confirmRecent} disabled={!selected}>
              <Check size={14} /> Use this database
            </Button>
          </>
        ) : (
          <>
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="primary" onClick={saveAndPick} disabled={saving}>
              {saving ? <Spinner /> : <Check size={14} />} Connect and use
            </Button>
          </>
        )
      }
    >
      <div className="flex gap-0.5 rounded-md p-0.5" style={{ background: 'var(--page)' }}>
        <TabButton
          active={tab === 'recent'}
          onClick={() => setTab('recent')}
          icon={<History size={13} />}
          label={`Recent${connections.length ? ` (${connections.length})` : ''}`}
        />
        <TabButton
          active={tab === 'new'}
          onClick={() => setTab('new')}
          icon={<Plus size={13} />}
          label="New connection"
        />
      </div>

      {tab === 'recent' ? (
        <div className="mt-3.5 flex flex-col gap-3">
          {connections.length > 4 && (
            <div className="relative">
              <Search
                size={13}
                className="absolute left-2.5 top-1/2 -translate-y-1/2"
                style={{ color: 'var(--text-3)' }}
              />
              <Input
                autoFocus
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Filter connections..."
                className="w-full pl-8"
              />
            </div>
          )}

          {recent.length === 0 ? (
            <div
              className="rounded-md border border-dashed px-4 py-8 text-center text-[12.5px]"
              style={{ borderColor: 'var(--line)', color: 'var(--text-2)' }}
            >
              {connections.length === 0
                ? 'No saved connections yet.'
                : 'Nothing matches, or every connection is already in use.'}
              <div className="mt-2">
                <Button variant="outline" onClick={() => setTab('new')}>
                  <Plus size={14} /> Connect to a database
                </Button>
              </div>
            </div>
          ) : (
            <div
              className="max-h-64 overflow-y-auto rounded-md border"
              style={{ borderColor: 'var(--line)' }}
            >
              {recent.map((c) => {
                const active = c.id === selectedId
                const colour = connectionColour(c)
                const name = c.label || c.server || 'Unnamed'

                return (
                  <div
                    key={c.id}
                    className="flex items-stretch border-b transition-colors last:border-b-0"
                    style={{
                      borderColor: 'var(--line-soft)',
                      borderLeft: `3px solid ${active ? colour.ink : colour.line}`,
                      background: active ? colour.tintStrong : colour.tint,
                    }}
                  >
                    <button
                      onClick={() => setSelectedId(c.id)}
                      onDoubleClick={() => {
                        setSelectedId(c.id)
                        onPick({ connectionId: c.id })
                      }}
                      className="flex min-w-0 flex-1 items-center gap-3 px-3 py-2 text-left"
                    >
                      <Server size={15} className="shrink-0" style={{ color: colour.ink }} />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-[13px] font-medium">{name}</span>
                        <span
                          className="block truncate font-mono text-[11px]"
                          style={{ color: 'var(--text-3)' }}
                        >
                          {c.useRawConnectionString
                            ? c.rawConnectionString
                            : `${c.server}${c.database ? ` / ${c.database}` : ''}`}
                        </span>
                      </span>
                      <span className="shrink-0 text-[11px]" style={{ color: 'var(--text-3)' }}>
                        {AUTH_LABELS[c.auth]?.split(' ')[0] ?? c.auth}
                      </span>
                      {active && <Check size={15} style={{ color: colour.ink }} />}
                    </button>

                    {confirmDelete === c.id ? (
                      <span className="flex shrink-0 items-center gap-1 pr-2">
                        <span className="text-[11.5px]" style={{ color: 'var(--text-2)' }}>
                          Forget it?
                        </span>
                        <Button size="sm" variant="danger" onClick={() => remove(c.id)} disabled={deleting}>
                          {deleting ? <Spinner size={11} /> : null} Delete
                        </Button>
                        <Button size="sm" onClick={() => setConfirmDelete(null)}>
                          Keep
                        </Button>
                      </span>
                    ) : (
                      <button
                        onClick={() => {
                          setDeleteError(null)
                          setConfirmDelete(c.id)
                        }}
                        className="my-1.5 mr-1.5 shrink-0 rounded px-1.5 transition-colors hover:bg-[var(--raised)]"
                        style={{ color: 'var(--text-3)' }}
                        title="Remove this saved connection"
                        aria-label={`Delete ${name}`}
                      >
                        <Trash2 size={14} />
                      </button>
                    )}
                  </div>
                )
              })}
            </div>
          )}

          {deleteError && (
            <div
              className="flex items-start gap-2 rounded-md px-3 py-2 text-[12.5px]"
              style={{
                background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
                color: 'var(--st-miss)',
              }}
            >
              <AlertTriangle size={15} className="mt-px shrink-0" />
              <span>{deleteError}</span>
            </div>
          )}

          {selected && selectedColour && (
            <div
              className="rounded-md border px-3 py-2.5"
              style={{ borderColor: selectedColour.line, background: selectedColour.tint }}
            >
              <div
                className="mb-1.5 flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wide"
                style={{ color: 'var(--text-3)' }}
              >
                <Database size={12} style={{ color: selectedColour.ink }} /> Database
                {loadingDbs && <Spinner size={11} />}
              </div>

              {databases && databases.length > 0 ? (
                <select
                  value={database}
                  onChange={(e) => setDatabase(e.target.value)}
                  className="w-full rounded-md border px-2 py-1.5 text-[13px] outline-none"
                  style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
                >
                  {databases.map((d) => (
                    <option key={d.name} value={d.name} disabled={!d.hasAccess}>
                      {d.name}
                      {d.hasAccess ? '' : '  (no access)'}
                    </option>
                  ))}
                </select>
              ) : (
                <Input
                  value={database}
                  onChange={(e) => setDatabase(e.target.value)}
                  placeholder="database name"
                  className="w-full"
                />
              )}

              <div className="mt-1.5 text-[11px]" style={{ color: 'var(--text-3)' }}>
                Same server, different database? Change it here - the saved connection is not altered.
                This tint is how this database is marked everywhere else in the app.
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="mt-3.5">
          <ConnectionForm form={form} onChange={setForm} />
          {saveError && (
            <div
              className="mt-3 rounded-md px-3 py-2 text-[12.5px]"
              style={{
                background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
                color: 'var(--st-miss)',
              }}
            >
              {saveError}
            </div>
          )}
          <div className="mt-2 text-[11.5px]" style={{ color: 'var(--text-3)' }}>
            This connection is saved so it appears under Recent next time.
          </div>
        </div>
      )}
    </Modal>
  )
}

function TabButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
}) {
  return (
    <button
      onClick={onClick}
      className="inline-flex flex-1 items-center justify-center gap-1.5 rounded px-3 py-1.5 text-[12.5px] font-medium transition-colors"
      style={{
        background: active ? 'var(--surface)' : 'transparent',
        color: active ? 'var(--text)' : 'var(--text-3)',
      }}
    >
      {icon}
      {label}
    </button>
  )
}
