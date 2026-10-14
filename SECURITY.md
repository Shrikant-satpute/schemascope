# Security

SchemaScope connects to production databases with real credentials. That makes
security a feature of the product, not paperwork around it.

## Reporting a vulnerability

Please report privately, not in a public issue.

- Open a [security advisory](https://github.com/Shrikant-satpute/schemascope/security/advisories/new), or
- email **security@shadowmark.in**

Expect a first reply within 72 hours. Please include what you did, what you
expected, and what happened instead. If a fix is warranted you will be credited
in the release notes unless you would rather not be.

## What SchemaScope guarantees

**Read-only by default.** Every time SchemaScope starts, it only reads.
Comparing, creating scripts, exporting and sharing issue nothing but reads
against your databases. The one path that changes a database is the opt-in,
session-only **Read & write mode** described [below](#write-mode); until you
turn it on in the running app, that path refuses to run.

**Nothing leaves your machine unless you share it.** This is enforced rather
than promised:

- The page runs under `Content-Security-Policy: default-src 'self'` with
  `connect-src 'self'`, so the browser engine itself refuses any request to an
  outside host.
- The WebView2 window declines to navigate anywhere except its own loopback
  origin.
- There is no telemetry, no update check, and no analytics.

**Sharing is opt-in, and it is the one exception.** Nothing listens on the
network until you click **Share** on a finished comparison. Then a separate,
read-only listener on port 5280 serves a snapshot of that comparison to anyone
on your LAN who has the link:

- It only answers a handful of `GET` routes for the shared snapshot. Your
  connections, passwords, history and the compare and script routes are not on
  it at all; they stay on the token-protected loopback API.
- Each link carries a 128-bit random token, compared in constant time. Wrong
  guesses and all other traffic are rate limited, and requests addressed to any
  host name other than this machine's are rejected.
- The snapshot never goes back to a database and never contains a connection
  string or password. Driver error messages are scrubbed before it is written.
- The listener stops when you revoke the last link, when the last one expires
  (after seven days), and when you close SchemaScope.
- **Allow through firewall** on the Share tab adds one inbound rule, after a UAC
  prompt: TCP port 5280, private and domain networks only, callers on the local
  subnet only, and only for the SchemaScope exe. It also removes any earlier copy
  of that rule and any rule Windows made for the exe.
- The first time sharing starts, Windows Defender Firewall may show its own
  "allow access" prompt. **Cancel it and use the Share tab's button instead.**
  Accepting it makes Windows create its own rule, which allows every port from
  any address. Cancelling makes Windows create a Block rule; the Share tab's
  button replaces that too.
- The firewall rule stays after SchemaScope closes. **Remove firewall rule** on
  the Share tab takes it away again (another UAC prompt). Do that before you
  uninstall: the per-user uninstaller has no administrator rights, so it cannot
  remove the rule. From an administrator command prompt, this does the same:
  `netsh advfirewall firewall delete rule name="SchemaScope share"`.
- Uninstalling deletes the shared-link snapshots in
  `%LOCALAPPDATA%\SchemaScope\shares` and the definition cache in
  `%LOCALAPPDATA%\SchemaScope\cache`.

A link does hand over the full text of every object in the comparison, plus the
server and database names, over plain HTTP. Share it the way you would share the
`.sql` files themselves. Everything else stays on the machine.

**Saved passwords are encrypted at rest** with Windows DPAPI, scoped to your
Windows account. Another user on the same PC cannot read them even with the
database file in hand. They are stored in
`%LOCALAPPDATA%\SchemaScope\schemascope.db`.

**The local API is authenticated.** SchemaScope hosts its UI on a loopback port,
which is not a boundary on its own: any local process can find the port, and a
web page you merely visit can reach it by DNS rebinding. So:

- Every `/api` request must carry a token minted at startup and handed only to
  the app's own window. It travels in a request header; the query-string form
  that `EventSource` needs is accepted on `GET` only, so a web page cannot fire
  a state-changing request at the API even if it knew the token. The release
  build has no fixed token, even in its `--server` developer mode.
- Requests addressed to any host other than the loopback origin are rejected,
  which is what closes the rebinding path.
- A stored password is only ever released for the exact server, login and auth
  mode it was saved against. Change any of them and the password must be
  retyped, so a saved credential cannot be redirected to a server of someone
  else's choosing. Saving a connection follows the same rule.
- Folder exports and generated scripts are never written to a network
  (`\\host\share`) path, because probing one offers your Windows credentials to
  that host.

## Write mode

From 1.2, SchemaScope can apply the scripts it generates instead of leaving that
to you. This is the deliberate exception to "read-only", and it is gated:

**What it can do.** Run the generated script files of one comparison against
one of that comparison's targets, using that target's connection exactly as you
set it up. Nothing else: there is no free-form query box, the source database is
never an apply target, and anything held back in `99_review` is never applied.

**The gates, in the order the server checks them:**

- **Write mode is on.** It lives only in the running app's memory: off on every
  launch, never saved to disk, switched on through a confirmation dialog, and
  off again after 30 idle minutes or when you press **Turn off** on the amber
  strip that shows while it is on. Turning it on is a JSON `POST` that carries
  the startup token in its header, so a web page cannot do it.
- **The plan is the one you reviewed.** An apply names a plan the server made
  when it generated the scripts, for that comparison and that target, and plans
  expire after two hours. Each file's SHA-256 is recorded when it is written and
  checked again before anything runs; one edited file and nothing runs.
- **You typed the database name.** It must match the target connection's
  database, and is checked again with `SELECT DB_NAME()` on the opened
  connection before the first statement.
- **The server is not protected.** A connection marked
  **Protected — never apply scripts here** is refused, re-read from your saved
  connections at the moment of the apply, and so is any target on the same
  server as a protected connection (for a raw connection string, the server
  named in it).
- **Data loss is a separate opt-in.** Changes that can lose data are only
  generated when you ask, which needs write mode on, and applying them needs a
  second explicit acceptance.
- **One apply per target at a time.** A second one is refused while the first
  runs, however the server's name is spelled: the lock uses the name the server
  reports for itself.

**How it runs.** Each file runs in its own transaction; the first error rolls
that file back and stops the run. Files that already committed stay applied,
and the app says so plainly. **Stop** takes effect after the current file;
**Cancel this file now**, or turning write mode off, also cancels the file that
is running, which is rolled back - nothing commits once the app says read-only.
If a file cannot be written to the apply log, the run stops after it.

**What is recorded.** Every apply is written to the local settings database:
when, the target's label, server, database and login, the sections, and each
file's result. Error text is scrubbed; no password or connection string is
stored.

**What a shared link gets.** Nothing. The LAN viewer on port 5280 has no write
routes and never shows the write controls, whether or not write mode is on.

**Residual risk.** An apply runs DDL as the target connection's login, so it
can do anything that login can. Use a login with no more rights than the
change needs, and rehearse on a restored copy first. While write mode is on,
malware already running as your Windows user could in principle drive the same
local API the window uses — the same exposure as an open SSMS session under
your account. Write mode being off on every launch and timing out when idle
keeps that window short.

## Known accepted risks

**The bundled Monaco editor requires `unsafe-eval` and `unsafe-inline` for
scripts.** The CSP allows both for `script-src`. `connect-src` remains `'self'`,
so this cannot be turned into exfiltration; the practical risk is limited to the
schema text you already loaded.

**Release binaries are not yet code-signed.** Windows SmartScreen will warn on
first run. Every release publishes SHA-256 checksums, and the binaries are built
in public by GitHub Actions from a tagged commit, so you can verify what you
downloaded matches what was built. See the release notes for the current hash.

## Supported versions

Fixes land on the latest release. There are no long-term support branches while
the project is pre-1.0.
