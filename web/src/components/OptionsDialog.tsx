import { useEffect, useState } from 'react'
import { Save, Trash2 } from 'lucide-react'
import { api } from '../api'
import type { CompareOptions, CompareProfile, DbObjectType } from '../types'
import { Button, Field, Input, Modal, Select, Toggle } from './ui'

const TYPE_LABELS: Record<DbObjectType, string> = {
  schema: 'Schemas',
  table: 'Tables',
  view: 'Views',
  storedProcedure: 'Procedures',
  scalarFunction: 'Scalar functions',
  tableValuedFunction: 'Table functions',
  aggregateFunction: 'Aggregates',
  trigger: 'Triggers',
  tableType: 'Table types',
  userDefinedType: 'User types',
  sequence: 'Sequences',
  synonym: 'Synonyms',
}

const ALL_TYPES = Object.keys(TYPE_LABELS) as DbObjectType[]

/** Ignore rules whose default is "on". Used to badge how many are non-default. */
const DEFAULTS: Partial<Record<keyof CompareOptions, boolean>> = {
  ignoreComments: false,
  ignoreCase: false,
  useSmartParseCompare: true,
  ignoreCollation: true,
  ignoreFillFactor: true,
  ignoreIndexPadding: true,
  ignoreIdentitySeed: true,
  ignoreSystemNamedConstraints: true,
  ignoreColumnOrder: false,
  ignoreNotForReplication: true,
  ignoreFileGroups: true,
  includeSystemObjects: false,
}

export function countNonDefault(o: CompareOptions): number {
  let n = 0
  for (const [k, v] of Object.entries(DEFAULTS)) {
    if ((o as unknown as Record<string, boolean>)[k] !== v) n++
  }
  if (o.includedTypes.length !== ALL_TYPES.length) n++
  if (o.excludedNamePatterns.length > 0) n++
  return n
}

export function OptionsDialog({
  options,
  onClose,
  onApply,
}: {
  options: CompareOptions
  onClose: () => void
  onApply: (o: CompareOptions) => void
}) {
  const [form, setForm] = useState<CompareOptions>(structuredClone(options))
  const [profiles, setProfiles] = useState<CompareProfile[]>([])
  const [profileName, setProfileName] = useState('')

  useEffect(() => {
    api.profiles.list().then(setProfiles).catch(() => setProfiles([]))
  }, [])

  const set = <K extends keyof CompareOptions>(k: K, v: CompareOptions[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const toggleType = (t: DbObjectType) =>
    set(
      'includedTypes',
      form.includedTypes.includes(t)
        ? form.includedTypes.filter((x) => x !== t)
        : [...form.includedTypes, t],
    )

  async function saveProfile() {
    if (!profileName.trim()) return
    const saved = await api.profiles.save({
      id: '',
      name: profileName.trim(),
      options: form,
      createdUtc: new Date().toISOString(),
    })
    setProfiles((p) => [...p.filter((x) => x.id !== saved.id), saved])
    setProfileName('')
  }

  async function deleteProfile(id: string) {
    await api.profiles.remove(id)
    setProfiles((p) => p.filter((x) => x.id !== id))
  }

  return (
    <Modal
      title="Compare options"
      subtitle="Anything switched off here is still reported as 'formatting only' rather than hidden - nothing ever disappears silently."
      onClose={onClose}
      width={760}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => onApply(form)}>
            Apply
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-2 gap-x-6 gap-y-4">
        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Code comparison
          </h3>
          <Toggle
            checked={form.useSmartParseCompare}
            onChange={(v) => set('useSmartParseCompare', v)}
            label="Smart compare"
            hint="Parse near-identical bodies and re-print them one way, so [dbo].[x] vs dbo.x is not a difference."
          />
          <Toggle
            checked={form.ignoreComments}
            onChange={(v) => set('ignoreComments', v)}
            label="Ignore comments"
            hint="Treat objects that differ only in comments as identical."
          />
          <Toggle
            checked={form.ignoreCase}
            onChange={(v) => set('ignoreCase', v)}
            label="Ignore letter case"
          />
        </section>

        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Table detail
          </h3>
          <Toggle
            checked={form.ignoreSystemNamedConstraints}
            onChange={(v) => set('ignoreSystemNamedConstraints', v)}
            label="Ignore auto-generated constraint names"
            hint="DF__Table__Col__1A2B3C differs on every server for no real reason."
          />
          <Toggle
            checked={form.ignoreCollation}
            onChange={(v) => set('ignoreCollation', v)}
            label="Ignore column collation"
          />
          <Toggle
            checked={form.ignoreIdentitySeed}
            onChange={(v) => set('ignoreIdentitySeed', v)}
            label="Ignore identity seed and increment"
          />
          <Toggle
            checked={form.ignoreColumnOrder}
            onChange={(v) => set('ignoreColumnOrder', v)}
            label="Ignore column order"
          />
        </section>

        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Index and storage
          </h3>
          <Toggle
            checked={form.ignoreFillFactor}
            onChange={(v) => set('ignoreFillFactor', v)}
            label="Ignore fill factor"
          />
          <Toggle
            checked={form.ignoreIndexPadding}
            onChange={(v) => set('ignoreIndexPadding', v)}
            label="Ignore index padding"
          />
          <Toggle
            checked={form.ignoreFileGroups}
            onChange={(v) => set('ignoreFileGroups', v)}
            label="Ignore filegroups"
          />
          <Toggle
            checked={form.includeSystemObjects}
            onChange={(v) => set('includeSystemObjects', v)}
            label="Include system objects"
            hint="Off by default. Turning this on adds a lot of noise."
          />
        </section>

        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Object types
          </h3>
          <div className="grid grid-cols-2 gap-x-2">
            {ALL_TYPES.map((t) => (
              <label key={t} className="flex cursor-pointer items-center gap-1.5 py-0.5 text-[12px]">
                <input
                  type="checkbox"
                  checked={form.includedTypes.includes(t)}
                  onChange={() => toggleType(t)}
                />
                {TYPE_LABELS[t]}
              </label>
            ))}
          </div>
          <div className="mt-1 flex gap-2 text-[11.5px]">
            <button className="hover:underline" style={{ color: 'var(--accent)' }} onClick={() => set('includedTypes', ALL_TYPES)}>
              all
            </button>
            <button
              className="hover:underline"
              style={{ color: 'var(--accent)' }}
              onClick={() => set('includedTypes', ['table', 'view', 'tableType', 'userDefinedType', 'sequence'])}
            >
              structure only
            </button>
            <button
              className="hover:underline"
              style={{ color: 'var(--accent)' }}
              onClick={() =>
                set('includedTypes', [
                  'storedProcedure',
                  'scalarFunction',
                  'tableValuedFunction',
                  'aggregateFunction',
                  'trigger',
                  'view',
                ])
              }
            >
              code only
            </button>
          </div>
        </section>

        <section className="col-span-2">
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Skip these objects
          </h3>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Excluded schemas" hint="Comma separated.">
              <Input
                value={form.excludedSchemas.join(', ')}
                onChange={(e) =>
                  set('excludedSchemas', e.target.value.split(',').map((s) => s.trim()).filter(Boolean))
                }
              />
            </Field>
            <Field label="Name patterns to skip" hint="Comma separated, * and ? allowed. e.g. tmp_*, *_bak">
              <Input
                value={form.excludedNamePatterns.join(', ')}
                onChange={(e) =>
                  set('excludedNamePatterns', e.target.value.split(',').map((s) => s.trim()).filter(Boolean))
                }
              />
            </Field>
          </div>
        </section>

        <section className="col-span-2 border-t pt-3" style={{ borderColor: 'var(--line)' }}>
          <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--text-3)' }}>
            Saved profiles
          </h3>

          <div className="flex gap-2">
            <Select
              className="min-w-48"
              value=""
              onChange={(e) => {
                const p = profiles.find((x) => x.id === e.target.value)
                if (p) setForm(structuredClone(p.options))
              }}
            >
              <option value="">- load a profile -</option>
              {profiles.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </Select>

            <Input
              placeholder="Name these settings..."
              value={profileName}
              onChange={(e) => setProfileName(e.target.value)}
              className="flex-1"
            />
            <Button variant="outline" onClick={saveProfile} disabled={!profileName.trim()}>
              <Save size={14} /> Save
            </Button>
          </div>

          {profiles.length > 0 && (
            <div className="mt-2 flex flex-wrap gap-1.5">
              {profiles.map((p) => (
                <span
                  key={p.id}
                  className="inline-flex items-center gap-1 rounded border py-0.5 pl-2 pr-1 text-[11.5px]"
                  style={{ borderColor: 'var(--line)' }}
                >
                  {p.name}
                  <button
                    onClick={() => deleteProfile(p.id)}
                    className="rounded p-0.5 hover:bg-[var(--raised)]"
                    style={{ color: 'var(--st-miss)' }}
                    aria-label={`Delete ${p.name}`}
                  >
                    <Trash2 size={11} />
                  </button>
                </span>
              ))}
            </div>
          )}
        </section>
      </div>
    </Modal>
  )
}
