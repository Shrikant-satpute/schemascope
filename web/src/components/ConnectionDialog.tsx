import { useState } from 'react'
import { api } from '../api'
import type { ConnectionSettings } from '../types'
import { Button, Modal, Spinner } from './ui'
import { ConnectionForm, BLANK_CONNECTION } from './ConnectionForm'

/** Add or edit a saved connection from the Connections screen. */
export function ConnectionDialog({
  initial,
  onClose,
  onSaved,
}: {
  initial?: ConnectionSettings
  onClose: () => void
  onSaved: (c: ConnectionSettings) => void
}) {
  const [form, setForm] = useState<Partial<ConnectionSettings>>(initial ?? BLANK_CONNECTION)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function save() {
    if (!form.server && !form.useRawConnectionString) {
      setError('Enter a server name.')
      return
    }
    setSaving(true)
    setError(null)
    try {
      onSaved(await api.connections.save(form))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setSaving(false)
    }
  }

  return (
    <Modal
      title={initial ? 'Edit connection' : 'Add connection'}
      subtitle="Saved on this PC only. The password is encrypted for your Windows account and never leaves the machine."
      onClose={onClose}
      width={640}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={save} disabled={saving}>
            {saving ? <Spinner /> : null} Save
          </Button>
        </>
      }
    >
      <ConnectionForm form={form} onChange={setForm} isEdit={!!initial} />

      {error && (
        <div
          className="mt-3 rounded-md px-3 py-2 text-[12.5px]"
          style={{
            background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
            color: 'var(--st-miss)',
          }}
        >
          {error}
        </div>
      )}
    </Modal>
  )
}
