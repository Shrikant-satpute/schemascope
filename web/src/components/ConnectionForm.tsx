import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Database, ShieldCheck } from 'lucide-react'
import { api } from '../api'
import type { AuthMode, ConnectionSettings, ConnectionTestResult, DatabaseEngine, DatabaseListItem, SslMode } from '../types'
import { blankConnection, engineOf, needsUser as takesUser, selectableEngines, useEngines } from '../engines'
import { Button, Field, Input, Select, Spinner, Toggle } from './ui'

/**
 * The connection fields, shared by the Connections screen and by the picker
 * that opens from "Select source". Test and Browse live next to the fields
 * rather than in a dialog footer, so they work the same wherever the form is
 * embedded. Every engine-specific word comes from the engine catalogue
 * (engines.ts); the Engine select only appears once there is more than one
 * engine to pick.
 */
export function ConnectionForm({
  form,
  onChange,
  isEdit,
  compact,
}: {
  form: Partial<ConnectionSettings>
  onChange: (f: Partial<ConnectionSettings>) => void
  isEdit?: boolean
  compact?: boolean
}) {
  const [test, setTest] = useState<ConnectionTestResult | null>(null)
  const [testing, setTesting] = useState(false)
  const [databases, setDatabases] = useState<DatabaseListItem[] | null>(null)
  const [loadingDbs, setLoadingDbs] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [engineChanged, setEngineChanged] = useState(false)
  const engines = useEngines()
  const engine = engineOf(engines, form.engine)
  const pickable = selectableEngines(engines)
  const text = engine.form

  const set = <K extends keyof ConnectionSettings>(key: K, value: ConnectionSettings[K]) => {
    onChange({ ...form, [key]: value })
    setTest(null)
  }

  // Another engine is another destination: its sign-in and switches start over, and the
  // server will not release a stored password to it anyway.
  function changeEngine(to: DatabaseEngine) {
    const blank = blankConnection(engineOf(engines, to))
    onChange({
      ...form,
      engine: to,
      auth: blank.auth,
      password: '',
      port: null,
      ssl: blank.ssl ?? null,
      encrypt: blank.encrypt,
      trustServerCertificate: blank.trustServerCertificate,
      applicationIntentReadOnly: false,
    })
    setEngineChanged(true)
    setTest(null)
  }

  const needsUser = takesUser(engine, form.auth)

  // The database list belongs to one server. Changing how we connect makes it stale.
  useEffect(() => {
    setDatabases(null)
  }, [form.engine, form.server, form.port, form.auth, form.userName, form.useRawConnectionString])

  async function runTest() {
    setTesting(true)
    setError(null)
    try {
      setTest(await api.connections.test(form))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setTesting(false)
    }
  }

  async function browseDatabases() {
    setLoadingDbs(true)
    setError(null)
    try {
      setDatabases(await api.connections.databases(form))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoadingDbs(false)
    }
  }

  return (
    <div className="flex flex-col gap-3.5">
      {pickable.length > 1 && (
        <Field
          label="Engine"
          hint={engineChanged ? 'Changing the engine clears the password: it will need re-entering.' : undefined}
        >
          <Select value={engine.engine} onChange={(e) => changeEngine(e.target.value as DatabaseEngine)}>
            {pickable.map((e) => (
              <option key={e.engine} value={e.engine}>
                {e.displayName}
                {e.preview ? ' (preview)' : ''}
              </option>
            ))}
          </Select>
        </Field>
      )}

      <div className="flex gap-1 rounded-md p-0.5" style={{ background: 'var(--page)' }}>
        {[
          { v: false, label: 'Connection properties' },
          { v: true, label: 'Connection string' },
        ].map((m) => (
          <button
            key={String(m.v)}
            onClick={() => set('useRawConnectionString', m.v)}
            className="flex-1 rounded px-3 py-1.5 text-[12.5px] font-medium transition-colors"
            style={{
              background: !!form.useRawConnectionString === m.v ? 'var(--surface)' : 'transparent',
              color: !!form.useRawConnectionString === m.v ? 'var(--text)' : 'var(--text-3)',
            }}
          >
            {m.label}
          </button>
        ))}
      </div>

      <Field label="Name" hint="Shown on the dashboard cards and as the matrix column header.">
        <Input
          value={form.label ?? ''}
          placeholder="e.g. Dev, QA, Client A prod"
          onChange={(e) => set('label', e.target.value)}
        />
      </Field>

      {form.useRawConnectionString ? (
        <Field label="Connection string" hint={text.connectionStringHint}>
          <textarea
            rows={3}
            value={form.rawConnectionString ?? ''}
            onChange={(e) => set('rawConnectionString', e.target.value)}
            placeholder={text.connectionStringPlaceholder}
            className="rounded-md border px-2.5 py-2 font-mono text-[12px] outline-none"
            style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
          />
        </Field>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3">
            <Field label={text.serverLabel} hint={text.serverHint}>
              <Input
                value={form.server ?? ''}
                placeholder={text.serverPlaceholder}
                onChange={(e) => set('server', e.target.value)}
              />
            </Field>
            <Field label="Authentication">
              <Select value={form.auth} onChange={(e) => set('auth', e.target.value as AuthMode)}>
                {engine.auth.map((a) => (
                  <option key={a.mode} value={a.mode}>
                    {a.label}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

          {(text.port || text.sslMode) && (
            <div className="grid grid-cols-2 gap-3">
              {text.port && (
                <Field label="Port" hint={engine.defaultPort ? `Empty = ${engine.defaultPort}` : undefined}>
                  <Input
                    type="number"
                    min={1}
                    max={65535}
                    value={form.port ?? ''}
                    placeholder={engine.defaultPort ? String(engine.defaultPort) : ''}
                    onChange={(e) => set('port', e.target.value === '' ? null : Number(e.target.value))}
                    className="w-28"
                  />
                </Field>
              )}
              {text.sslMode && <SslModeField form={form} set={set} />}
            </div>
          )}

          {needsUser && (
            <div className="grid grid-cols-2 gap-3">
              <Field label="User name">
                <Input value={form.userName ?? ''} onChange={(e) => set('userName', e.target.value)} />
              </Field>
              <Field label="Password">
                <Input
                  type="password"
                  value={form.password ?? ''}
                  placeholder={isEdit ? 'unchanged' : ''}
                  onChange={(e) => set('password', e.target.value)}
                />
              </Field>
            </div>
          )}
        </>
      )}

      <Field label="Database name">
        <div className="flex gap-2">
          {databases ? (
            <Select
              className="flex-1"
              value={form.database ?? ''}
              onChange={(e) => set('database', e.target.value)}
            >
              <option value="">- choose -</option>
              {databases.map((d) => (
                <option key={d.name} value={d.name} disabled={!d.hasAccess}>
                  {d.name}
                  {d.hasAccess ? '' : '  (no access)'}
                </option>
              ))}
            </Select>
          ) : (
            <Input
              className="flex-1"
              value={form.database ?? ''}
              placeholder={text.databasePlaceholder}
              onChange={(e) => set('database', e.target.value)}
            />
          )}
          <Button variant="outline" onClick={browseDatabases} disabled={loadingDbs}>
            {loadingDbs ? <Spinner /> : <Database size={14} />} Browse
          </Button>
        </div>
      </Field>

      {!compact && (
        <details className="rounded-md border" style={{ borderColor: 'var(--line)' }}>
          <summary className="cursor-pointer px-3 py-2 text-[12.5px]" style={{ color: 'var(--text-2)' }}>
            Advanced
          </summary>
          <div className="flex flex-col gap-0.5 px-1 pb-2">
            {text.trustServerCertificate && (
              <Toggle
                checked={!!form.trustServerCertificate}
                onChange={(v) => set('trustServerCertificate', v)}
                label="Trust server certificate"
                hint={
                  text.sslMode
                    ? 'Encrypts without checking the certificate (SSL mode Require); with SSL mode Disable, the explicit opt-in to no TLS. Only for a server you trust on a network you trust.'
                    : 'Needed for most internal servers that use a self-signed certificate.'
                }
              />
            )}
            {text.encrypt && (
              <Toggle checked={!!form.encrypt} onChange={(v) => set('encrypt', v)} label="Encrypt the connection" />
            )}
            {text.readOnlyIntent && (
              <Toggle
                checked={!!form.applicationIntentReadOnly}
                onChange={(v) => set('applicationIntentReadOnly', v)}
                label="Read-only intent"
                hint="Routes to a readable secondary on an availability group."
              />
            )}
            <Toggle
              checked={!!form.protectedServer}
              onChange={(v) => set('protectedServer', v)}
              label="Protected — never apply scripts here"
              hint="Scripts for it are only written to disk. SchemaScope will not apply anything to it, even with write mode on."
            />
            <div className="px-2 pt-1">
              <Field label="Connect timeout (seconds)">
                <Input
                  type="number"
                  min={1}
                  max={300}
                  value={form.connectTimeoutSeconds ?? 15}
                  onChange={(e) => set('connectTimeoutSeconds', Number(e.target.value))}
                  className="w-24"
                />
              </Field>
            </div>
          </div>
        </details>
      )}

      <div className="flex items-center gap-2">
        <Button variant="outline" onClick={runTest} disabled={testing}>
          {testing ? <Spinner /> : <ShieldCheck size={14} />} Test connection
        </Button>
      </div>

      {error && (
        <div
          className="flex items-start gap-2 rounded-md px-3 py-2 text-[12.5px]"
          style={{
            background: 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
            color: 'var(--st-miss)',
          }}
        >
          <AlertTriangle size={15} className="mt-px shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {test && <TestReport result={test} />}
    </div>
  )
}

/**
 * TLS for engines that take an SSL mode. Verify full is the default; connecting without checking the certificate
 * is the separate, explicit "Trust server certificate" switch (Require and Prefer never check it, and Disable has
 * no TLS at all, so the server refuses them without that switch).
 */
function SslModeField({
  form,
  set,
}: {
  form: Partial<ConnectionSettings>
  set: <K extends keyof ConnectionSettings>(key: K, value: ConnectionSettings[K]) => void
}) {
  const mode: SslMode = form.ssl ?? 'verifyFull'
  const trusted = !!form.trustServerCertificate && mode !== 'disable'
  const options: { v: SslMode; label: string }[] = [
    { v: 'verifyFull', label: 'Verify full (certificate and host name)' },
    { v: 'verifyCa', label: 'Verify CA (certificate only)' },
    { v: 'disable', label: 'Disable (no TLS)' },
  ]
  // a mode set some other way (e.g. by an older form) still shows as itself
  if (!options.some((o) => o.v === mode)) options.push({ v: mode, label: mode === 'require' ? 'Require (no certificate check)' : 'Prefer (no certificate check)' })
  return (
    <Field
      label="SSL mode"
      hint={
        mode === 'disable'
          ? form.trustServerCertificate
            ? 'Unencrypted: the password and every query cross the network in clear text.'
            : 'Unencrypted: the password and every query cross the network in clear text. Tick "Trust server certificate" under Advanced to connect this way on purpose.'
          : trusted
            ? 'Trust server certificate is on: encrypted, but the certificate is not checked.'
            : undefined
      }
    >
      <Select value={mode} onChange={(e) => set('ssl', e.target.value as SslMode)}>
        {options.map((o) => (
          <option key={o.v} value={o.v}>
            {o.label}
          </option>
        ))}
      </Select>
    </Field>
  )
}

export function TestReport({ result }: { result: ConnectionTestResult }) {
  const good = result.success
  return (
    <div
      className="rounded-md px-3 py-2.5 text-[12.5px]"
      style={{
        background: good
          ? 'color-mix(in srgb, var(--st-same) 12%, transparent)'
          : 'color-mix(in srgb, var(--st-miss) 12%, transparent)',
      }}
    >
      <div
        className="flex items-center gap-2 font-medium"
        style={{ color: good ? 'var(--st-same)' : 'var(--st-miss)' }}
      >
        {good ? <CheckCircle2 size={15} /> : <AlertTriangle size={15} />}
        {good ? `Connected in ${result.elapsedMs} ms` : 'Could not connect'}
      </div>

      {good && (
        <div className="mt-1.5 grid grid-cols-2 gap-x-4 gap-y-0.5" style={{ color: 'var(--text-2)' }}>
          <span>Server: {result.serverName}</span>
          <span>Database: {result.databaseName}</span>
          <span>Version: {result.productVersion}</span>
          <span>Objects: {result.objectCount.toLocaleString()}</span>
        </div>
      )}

      {result.message && !good && (
        <div className="mt-1" style={{ color: 'var(--text-2)' }}>
          {result.message}
        </div>
      )}

      {result.hint && (
        <div className="mt-2 rounded px-2 py-1.5" style={{ background: 'var(--page)', color: 'var(--text-2)' }}>
          {result.hint}
        </div>
      )}
    </div>
  )
}
