import { useCallback, useEffect, useState } from 'react'
import {
  AlertTriangle,
  ArrowRight,
  Check,
  Copy,
  Eye,
  Globe,
  HelpCircle,
  Play,
  Share2,
  ShieldAlert,
  Square,
  Trash2,
} from 'lucide-react'
import { api } from '../api'
import type { ShareStatus, ShareSummary } from '../types'
import { Button, EmptyState, Panel, Spinner } from './ui'

/** Often enough to feel live, rarely enough to be free. */
const POLL_MS = 3000

/**
 * What each link is actually worth to somebody else. The computer name only
 * resolves if the network runs a DNS server that knows this machine - on a
 * workgroup PC pointed at public DNS, it resolves nowhere, and the person you
 * sent it to gets "server IP address could not be found".
 */
const LINK_HINT: Record<string, string> = {
  lan: 'send this one',
  name: 'by computer name - only if your network can resolve it',
  virtual: 'a virtual adapter on this PC - your team cannot reach this',
}

/**
 * Sharing, from the owner's side.
 *
 * One row per published comparison: the link, how long it lives, and the
 * machines reading it right now. The listener that serves those links is a
 * separate one from the app's own - it only ever answers four read-only
 * requests, and it does not run at all unless something is shared.
 */
export function ShareView({
  hasResult,
  onGoToCompare,
  onToast,
}: {
  hasResult: boolean
  onGoToCompare: () => void
  onToast: (message: string) => void
}) {
  const [status, setStatus] = useState<ShareStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmStop, setConfirmStop] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setStatus(await api.shares.status())
    } catch {
      /* the window is closing, or the app is shutting down */
    }
  }, [])

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), POLL_MS)
    return () => clearInterval(timer)
  }, [load])

  async function act(work: () => Promise<unknown>, done?: string) {
    setBusy(true)
    try {
      await work()
      if (done) onToast(done)
    } catch (e) {
      onToast(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
      setConfirmStop(null)
      await load()
    }
  }

  async function copy(url: string) {
    await navigator.clipboard.writeText(url).catch(() => undefined)
    setCopied(url)
    setTimeout(() => setCopied(null), 1500)
  }

  if (!status) {
    return (
      <div
        className="flex h-full items-center justify-center gap-2 text-[13px]"
        style={{ color: 'var(--text-2)' }}
      >
        <Spinner /> Loading...
      </div>
    )
  }

  const shares = status.shares

  return (
    <div className="flex h-full flex-col overflow-y-auto px-6 py-5">
      <div className="mx-auto w-full max-w-4xl">
        <div className="mb-5 flex items-end justify-between gap-4">
          <div>
            <h1 className="text-[19px] font-semibold">Share</h1>
            <p className="mt-0.5 text-[12.5px]" style={{ color: 'var(--text-2)' }}>
              Give your team a link to a comparison you have already run. They can search, filter and
              read the differences. They cannot run a compare, see your connections, or export
              anything - the listener that serves these links has no route to any of that.
            </p>
          </div>

          {shares.length > 0 &&
            (status.sharing ? (
              <Button variant="outline" onClick={() => act(() => api.shares.stop(), 'Sharing stopped.')} disabled={busy}>
                <Square size={13} /> Stop sharing
              </Button>
            ) : (
              <Button variant="primary" onClick={() => act(() => api.shares.start(), 'Sharing is on.')} disabled={busy}>
                <Play size={14} /> Start sharing
              </Button>
            ))}
        </div>

        <StatusStrip
          status={status}
          onAllowFirewall={() => act(() => api.shares.firewall().then((r) => {
            onToast(r.message)
          }))}
          onRemoveFirewall={() => act(() => api.shares.removeFirewall().then((r) => {
            onToast(r.message)
          }))}
          busy={busy}
        />

        {shares.length === 0 ? (
          <Panel className="mt-4 h-72">
            <EmptyState
              icon={<Share2 size={30} />}
              title="Nothing is shared"
              body={
                hasResult
                  ? 'Press Share on the results toolbar and the link appears here.'
                  : 'Run a comparison first, then press Share on the results toolbar.'
              }
              action={
                !hasResult ? (
                  <Button variant="primary" onClick={onGoToCompare}>
                    <ArrowRight size={14} /> Go to Compare
                  </Button>
                ) : undefined
              }
            />
          </Panel>
        ) : (
          <div className="mt-4 grid gap-2.5">
            {shares.map((share) => (
              <ShareCard
                key={share.token}
                share={share}
                sharing={status.sharing}
                copied={copied}
                confirming={confirmStop === share.token}
                onCopy={copy}
                onConfirm={() => setConfirmStop(share.token)}
                onCancel={() => setConfirmStop(null)}
                onStop={() => act(() => api.shares.revoke(share.token), 'That link no longer works.')}
                busy={busy}
              />
            ))}
          </div>
        )}

        {shares.length > 0 && <Troubleshooting />}

        <p className="mt-4 text-[11.5px] leading-relaxed" style={{ color: 'var(--text-3)' }}>
          A link works only while this machine is awake and SchemaScope is open. Anyone on your
          network who has the link can read the full text of every object in that comparison - the
          link is the only lock on it. Links stop working after 7 days, or the moment you press Stop.
        </p>
      </div>
    </div>
  )
}

/**
 * The browser's own error message says which of the three things went wrong.
 * Reading it saves a lot of guessing, so the translation lives right here rather
 * than in somebody's memory.
 */
function Troubleshooting() {
  const [open, setOpen] = useState(false)

  const causes = [
    {
      error: 'server IP address could not be found',
      meaning:
        'Their PC could not look up the name. Send the link that starts with a number instead - the top one.',
    },
    {
      error: 'took too long to respond',
      meaning:
        'They are on a different network, or your Wi-Fi blocks devices from talking to each other. Both need sorting on the network, not here.',
    },
    {
      error: 'refused to connect',
      meaning: 'SchemaScope is closed on this machine, or sharing has been stopped.',
    },
  ]

  return (
    <div className="mt-4">
      <button
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-1.5 text-[12px] hover:underline"
        style={{ color: 'var(--accent)' }}
      >
        <HelpCircle size={13} />
        {open ? 'Hide' : 'Your team cannot open the link?'}
      </button>

      {open && (
        <Panel className="mt-2 px-4 py-3">
          <p className="mb-2 text-[12px]" style={{ color: 'var(--text-2)' }}>
            Ask them what the browser says under &ldquo;This site can&rsquo;t be reached&rdquo;. The
            wording tells you which problem it is.
          </p>
          <div className="grid gap-2">
            {causes.map((c) => (
              <div key={c.error} className="text-[12px]">
                <span className="font-mono text-[11.5px]" style={{ color: 'var(--st-miss)' }}>
                  &ldquo;{c.error}&rdquo;
                </span>
                <div style={{ color: 'var(--text-2)' }}>{c.meaning}</div>
              </div>
            ))}
          </div>
        </Panel>
      )}
    </div>
  )
}

function StatusStrip({
  status,
  onAllowFirewall,
  onRemoveFirewall,
  busy,
}: {
  status: ShareStatus
  onAllowFirewall: () => void
  onRemoveFirewall: () => void
  busy: boolean
}) {
  return (
    <div className="grid gap-2.5">
      <Panel className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2.5 text-[12.5px]">
        <span className="inline-flex items-center gap-1.5">
          <span
            className="inline-block h-2 w-2 rounded-full"
            style={{ background: status.sharing ? 'var(--st-same)' : 'var(--text-3)' }}
          />
          {status.sharing ? `Listening on port ${status.port}` : 'Not listening'}
        </span>

        <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--text-2)' }}>
          <Globe size={13} /> {status.addresses.map((a) => a.value).join(' · ')}
        </span>

        {status.error && (
          <span className="inline-flex items-center gap-1.5" style={{ color: 'var(--st-miss)' }}>
            <AlertTriangle size={13} /> {status.error}
          </span>
        )}
      </Panel>

      {!status.firewallAllowed && (
        <Panel
          className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-[12.5px]"
          style={{
            background: 'color-mix(in srgb, var(--st-diff) 10%, transparent)',
            borderColor: 'color-mix(in srgb, var(--st-diff) 40%, transparent)',
          }}
        >
          <ShieldAlert size={16} className="shrink-0" style={{ color: 'var(--st-diff)' }} />
          <span className="min-w-0 flex-1">
            <b>Windows Firewall has not been told to allow this.</b> Until it is, links will work on
            this machine and nowhere else. Windows will ask for administrator rights. If Windows
            shows its own "allow access" prompt when sharing starts, cancel it and use this button
            instead: Windows' rule opens every port to any address, ours opens one port to your
            local network on private and domain networks only, and replaces any rule Windows made.
            <span className="mt-1 block break-all font-mono text-[11px]" style={{ color: 'var(--text-3)' }}>
              {status.firewallCommand}
            </span>
          </span>
          <Button variant="outline" onClick={onAllowFirewall} disabled={busy}>
            Allow through firewall
          </Button>
        </Panel>
      )}

      {status.firewallAllowed && (
        <Panel className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-[12.5px]">
          <ShieldAlert size={16} className="shrink-0" style={{ color: 'var(--text-3)' }} />
          <span className="min-w-0 flex-1" style={{ color: 'var(--text-2)' }}>
            Windows Firewall lets other machines on your network reach port {status.port}. The rule
            stays after SchemaScope closes, so remove it when you are done sharing, and before you
            uninstall. Windows will ask for administrator rights.
          </span>
          <Button variant="outline" onClick={onRemoveFirewall} disabled={busy}>
            Remove firewall rule
          </Button>
        </Panel>
      )}
    </div>
  )
}

function ShareCard({
  share,
  sharing,
  copied,
  confirming,
  onCopy,
  onConfirm,
  onCancel,
  onStop,
  busy,
}: {
  share: ShareSummary
  sharing: boolean
  copied: string | null
  confirming: boolean
  onCopy: (url: string) => void
  onConfirm: () => void
  onCancel: () => void
  onStop: () => void
  busy: boolean
}) {
  return (
    <Panel className="px-4 py-3">
      <div className="flex flex-wrap items-start gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[13.5px] font-medium">{share.sourceLabel}</span>
            <ArrowRight size={14} style={{ color: 'var(--text-3)' }} />
            <span className="text-[13px]" style={{ color: 'var(--text-2)' }}>
              {share.targetLabels.join(', ')}
            </span>
          </div>

          <div className="mt-0.5 font-mono text-[11px]" style={{ color: 'var(--text-3)' }}>
            {share.sourceDetail}
          </div>

          <div
            className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-[11.5px]"
            style={{ color: 'var(--text-3)' }}
          >
            <span className="tabular-nums">{share.objectCount.toLocaleString()} objects</span>
            <span>compared {new Date(share.comparedUtc).toLocaleString()}</span>
            <span>expires {new Date(share.expiresUtc).toLocaleDateString()}</span>
            <span className="tabular-nums">{share.totalViews} opens</span>
          </div>
        </div>

        <div className="flex shrink-0 items-center gap-2">
          <span
            className="inline-flex items-center gap-1.5 rounded px-2 py-1 text-[12px] font-medium"
            style={
              share.viewersNow > 0
                ? { background: 'color-mix(in srgb, var(--st-same) 14%, transparent)', color: 'var(--st-same)' }
                : { background: 'var(--raised)', color: 'var(--text-3)' }
            }
          >
            <Eye size={13} /> {share.viewersNow} reading now
          </span>

          {confirming ? (
            <>
              <Button variant="danger" onClick={onStop} disabled={busy}>
                Stop it
              </Button>
              <Button onClick={onCancel}>Keep</Button>
            </>
          ) : (
            <Button variant="danger" onClick={onConfirm} title="Stop this link working" aria-label="Stop this link">
              <Trash2 size={14} />
            </Button>
          )}
        </div>
      </div>

      {sharing && share.urls.length > 0 ? (
        <div className="mt-2.5 grid gap-1">
          {share.urls.map((link, i) => {
            const best = i === 0
            return (
              <div
                key={link.url}
                className="flex items-center gap-2 rounded-md border px-2.5 py-1.5"
                style={{
                  borderColor: best ? 'var(--accent)' : 'var(--line)',
                  background: best ? 'var(--accent-soft)' : 'var(--surface-2)',
                  opacity: link.kind === 'virtual' ? 0.65 : 1,
                }}
              >
                <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{link.url}</span>
                <span
                  className="shrink-0 text-[10.5px]"
                  style={{ color: best ? 'var(--accent)' : 'var(--text-3)' }}
                >
                  {LINK_HINT[link.kind]}
                </span>
                <Button size="sm" variant="outline" onClick={() => onCopy(link.url)}>
                  {copied === link.url ? <Check size={13} /> : <Copy size={13} />}
                  {copied === link.url ? 'Copied' : 'Copy'}
                </Button>
              </div>
            )
          })}
        </div>
      ) : (
        <div className="mt-2.5 text-[12px]" style={{ color: 'var(--st-diff)' }}>
          Sharing is switched off, so this link is not answering. Press Start sharing to bring it back.
        </div>
      )}

      {share.viewers.length > 0 && (
        <div className="mt-2.5 border-t pt-2" style={{ borderColor: 'var(--line-soft)' }}>
          <div
            className="mb-1 text-[10.5px] font-medium uppercase tracking-wide"
            style={{ color: 'var(--text-3)' }}
          >
            Connected now
          </div>
          <div className="flex flex-wrap gap-1.5">
            {share.viewers.map((v) => (
              <span
                key={`${v.ipAddress}-${v.connectedUtc}`}
                className="inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 text-[11.5px]"
                style={{ borderColor: 'var(--line)', background: 'var(--surface-2)' }}
                title={`Connected ${new Date(v.connectedUtc).toLocaleTimeString()}`}
              >
                <span className="font-mono">{v.ipAddress}</span>
                <span style={{ color: 'var(--text-3)' }}>
                  since {new Date(v.connectedUtc).toLocaleTimeString()}
                </span>
              </span>
            ))}
          </div>
        </div>
      )}
    </Panel>
  )
}
