import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle2, Database, ShieldCheck } from 'lucide-react'
import { api } from '../api'
import type { AuthMode, ConnectionSettings, ConnectionTestResult, DatabaseListItem } from '../types'
import { Button, Field, Input, Select, Spinner, Toggle } from './ui'

export const AUTH_LABELS: Record<AuthMode, string> = {
  windows: 'Windows (current user)',
  sqlLogin: 'SQL login',
  azureAdPassword: 'Microsoft Entra - password',
  azureAdInteractive: 'Microsoft Entra - interactive (MFA)',
  azureAdDefault: 'Microsoft Entra - default',
}

export const BLANK_CONNECTION: Partial<ConnectionSettings> = {
  label: '',
  server: '',
  database: '',
  auth: 'windows',
  userName: '',
  password: '',
  encrypt: false,
  trustServerCertificate: true,
  connectTimeoutSeconds: 15,
  applicationIntentReadOnly: false,
  useRawConnectionString: false,
  rawConnectionString: '',
  protectedServer: false,
  colour: '',
}

/**
 * The connection fields, shared by the Connections screen and by the picker
 * that opens from "Select source". Test and Browse live next to the fields
 * rather than in a dialog footer, so they work the same wherever the form is
 * embedded.
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

  const set = <K extends keyof ConnectionSettings>(key: K, value: ConnectionSettings[K]) => {
    onChange({ ...form, [key]: value })
    setTest(null)
  }

  const needsUser = form.auth === 'sqlLogin' || form.auth === 'azureAdPassword'

  // The database list belongs to one server. Changing how we connect makes it stale.
  useEffect(() => {
    setDatabases(null)
  }, [form.server, form.auth, form.userName, form.useRawConnectionString])

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
        <Field label="Connection string" hint="Anything Microsoft.Data.SqlClient accepts.">
          <textarea
            rows={3}
            value={form.rawConnectionString ?? ''}
            onChange={(e) => set('rawConnectionString', e.target.value)}
            placeholder="Server=10.0.0.5,1433;Database=MyDb;User Id=sa;Password=***;TrustServerCertificate=true"
            className="rounded-md border px-2.5 py-2 font-mono text-[12px] outline-none"
            style={{ background: 'var(--page)', borderColor: 'var(--line)' }}
          />
        </Field>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Server name" hint="host, host\instance or host,port">
              <Input
                value={form.server ?? ''}
                placeholder="localhost  or  10.0.0.5,1433"
                onChange={(e) => set('server', e.target.value)}
              />
            </Field>
            <Field label="Authentication">
              <Select value={form.auth} onChange={(e) => set('auth', e.target.value as AuthMode)}>
                {Object.entries(AUTH_LABELS).map(([v, l]) => (
                  <option key={v} value={v}>
                    {l}
                  </option>
                ))}
              </Select>
            </Field>
          </div>

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
              placeholder="Platform_Core"
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
            <Toggle
              checked={!!form.trustServerCertificate}
              onChange={(v) => set('trustServerCertificate', v)}
              label="Trust server certificate"
              hint="Needed for most internal servers that use a self-signed certificate."
            />
            <Toggle checked={!!form.encrypt} onChange={(v) => set('encrypt', v)} label="Encrypt the connection" />
            <Toggle
              checked={!!form.applicationIntentReadOnly}
              onChange={(v) => set('applicationIntentReadOnly', v)}
              label="Read-only intent"
              hint="Routes to a readable secondary on an availability group."
            />
            <Toggle
              checked={!!form.protectedServer}
              onChange={(v) => set('protectedServer', v)}
              label="Mark as protected"
              hint="Flags a server you never want written to. SchemaScope never writes anyway."
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
