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

**It never writes to your databases.** There is no code path in SchemaScope that
issues anything but a read. Comparison output is a report; applying it is your
decision, in your own tools.

**Nothing leaves your machine.** This is enforced rather than promised:

- The page runs under `Content-Security-Policy: default-src 'self'` with
  `connect-src 'self'`, so the browser engine itself refuses any request to an
  outside host.
- The WebView2 window declines to navigate anywhere except its own loopback
  origin.
- There is no telemetry, no update check, and no analytics.

**Saved passwords are encrypted at rest** with Windows DPAPI, scoped to your
Windows account. Another user on the same PC cannot read them even with the
database file in hand. They are stored in
`%LOCALAPPDATA%\SchemaScope\schemascope.db`.

**The local API is authenticated.** SchemaScope hosts its UI on a loopback port,
which is not a boundary on its own: any local process can find the port, and a
web page you merely visit can reach it by DNS rebinding. So:

- Every `/api` request must carry a token minted at startup and handed only to
  the app's own window.
- Requests addressed to any host other than the loopback origin are rejected,
  which is what closes the rebinding path.
- A stored password is only ever released for the exact server, login and auth
  mode it was saved against. Change any of them and the password must be
  retyped, so a saved credential cannot be redirected to a server of someone
  else's choosing.

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
