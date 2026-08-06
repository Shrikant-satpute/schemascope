<p align="center">
  <img src="assets/logo.svg" width="72" height="72" alt="">
</p>

<h1 align="center">SchemaScope</h1>

<p align="center">
  <a href="https://github.com/Shrikant-satpute/schemascope/releases/latest/download/SchemaScope-Setup.exe"><b>Download for Windows</b></a>
  &nbsp;·&nbsp;
  <a href="https://schemascope.shadowmark.in">Website</a>
  &nbsp;·&nbsp;
  <a href="SECURITY.md">Security</a>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="Apache-2.0"></a>
  <a href="../../actions/workflows/ci.yml"><img src="../../actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4" alt="Windows 10 and 11">
</p>

A fast SQL Server schema comparison tool for Windows.

Compares **one source database against many targets at the same time** and shows
the result as a matrix, a drift dashboard, and a VS Code style diff.

**Read only.** There is no code in SchemaScope that writes to a database. It
produces scripts and reports; applying them is your decision, in your own tools.

---

## Why it exists

Visual Studio's Schema Compare builds a full semantic model of both databases
before it can tell you anything. On a large database that takes minutes, and it
only ever compares one pair at a time.

SchemaScope reads the system catalog in bulk instead, then hashes every object.
Measured on a real 1,189 object database against a 990 object copy:

| Stage | Time |
|---|---|
| Read both databases (in parallel) | 500 ms |
| Compare and classify | 296 ms |
| **Total** | **806 ms** |

Same work in Visual Studio: 60 to 180 seconds, for one target.

### How it gets there

1. **Read the catalog, not the model.** Two round trips per database: a version
   probe, then one batch that returns every result set we need. Never one query
   per object.
2. **Hash first, diff later.** Every object gets a SHA-256 of its normalized
   text. Matching hashes mean the object is identical and is never looked at
   again - typically 90% or more of the database.
3. **Compare tables as data, not text.** Columns, indexes and constraints are
   rendered from catalog metadata by the *same code* on both sides, so
   formatting can never produce a fake difference.
4. **All targets in parallel.** Four databases cost about as much wall clock
   time as one.
5. **Cheap gate before expensive checks.** The T-SQL parser only runs on bodies
   that are already nearly identical. Skipping it on the obviously-different
   ones cut the compare stage from 1693 ms to 296 ms.
6. **Stream to the screen.** Progress arrives per database as it happens.

---

## Six statuses, not two

Most tools tell you "same" or "different". That reports a re-indented procedure
as a change and buries the real ones.

| | Status | Meaning |
|:-:|---|---|
| `=` | Same | Identical to the source |
| `~` | Formatting | Same code, only layout, comments or bracket quoting moved |
| `≠` | Different | A real difference, with the changed line count |
| `✕` | Missing | In the source but not in this target |
| `+` | Extra | Only in this target - usually a local customisation |
| `?` | Unreadable | Encrypted, or the login lacks `VIEW DEFINITION` |

Nothing is ever hidden. An ignore rule downgrades an object to *Formatting*
rather than making it disappear.

Every status carries a symbol and a word as well as a colour, and the colours
are validated for colour vision deficiency in both light and dark mode.

---

## What it shows

- **Drift dashboard** - one tile per target: how much of the source it still
  matches, a breakdown meter, and counts you can click to filter.
- **"Drifted in exactly one target"** - objects that changed in a single
  environment. Almost always a local customisation rather than a missed release.
- **Matrix grid** - one row per object, one column per target, virtualized so
  thousands of rows stay smooth.
- **Diff panel** - Monaco (the VS Code editor) with real line numbers,
  side-by-side or inline. `F8` and `Shift+F8` walk the changes.
- **Table grid diff** - tables compare field by field. A text diff of a
  `CREATE TABLE` lights up the whole block when one column is renamed; the grid
  highlights only the cells that actually differ.
- **Export** - standalone HTML report, CSV, Markdown, or one `.sql` file per
  object into a folder.

---

## Privacy

Everything is local, and it is enforced rather than promised:

- Connections and passwords live in `%LOCALAPPDATA%\SchemaScope\schemascope.db`.
  Passwords are encrypted with Windows DPAPI under your account, so no other
  Windows user can read them.
- The UI is served from inside the exe. The page runs under a
  `Content-Security-Policy` of `default-src 'self'`, so the browser engine
  itself blocks any request to an outside host.
- The window refuses to navigate anywhere except its own loopback origin.
- No telemetry, no update check, no network calls of any kind.

---

## Installing

[**Download SchemaScope-Setup.exe**](https://github.com/Shrikant-satpute/schemascope/releases/latest/download/SchemaScope-Setup.exe)
and run it. It installs for your account only, so there is no administrator
prompt, and it offers a desktop shortcut.

Prefer nothing installed? [`SchemaScope.exe`](https://github.com/Shrikant-satpute/schemascope/releases/latest)
is the portable build - one self-contained file, no .NET install needed, nothing
to unpack, no files beside it.

WebView2 is required and ships with Windows 10 and 11. If it is somehow missing
you get a plain explanation and a download link.

### Windows will warn you on first run

The binaries are not code-signed yet, so SmartScreen shows *"Windows protected
your PC"*. Click **More info**, then **Run anyway**.

You do not have to take that on trust. Every release is built in public by
GitHub Actions from the tagged commit, and ships a `SHA256SUMS.txt`:

```powershell
Get-FileHash .\SchemaScope-Setup.exe -Algorithm SHA256
```

### Permissions it needs

A login that can read the catalog:

```sql
GRANT VIEW DEFINITION TO [your_login];
```

Without it SchemaScope still sees object names but not their bodies, and says so
per object instead of reporting a false difference. The connection test warns
you up front.

---

## Building

```powershell
.\build.ps1              # front end + single file exe -> dist\SchemaScope.exe
.\build.ps1 -SkipWeb     # C# only
```

Needs the .NET 9 SDK and Node 20+.

### Working on the front end

```powershell
# terminal 1 - the API on a fixed port
dotnet run --project src\SchemaScope.Shell -- --server --port 5199

# terminal 2 - Vite with hot reload, proxying /api to 5199
cd web
npm run dev
```

### Measuring the engine

The Phase 0 spike is still in the repo. It reports stage timings and, more
usefully, every object that differs *only* by formatting - each one is a
normalizer rule worth reviewing before trusting the tool on a new database.

```powershell
dotnet run --project src\SchemaScope.Spike -c Release -- `
  -s "Server=localhost;Database=Dev;Integrated Security=true;TrustServerCertificate=true" `
  -t "Server=localhost;Database=Prod;Integrated Security=true;TrustServerCertificate=true"
```

---

## Layout

```
src/
  SchemaScope.Core        models, T-SQL normalizer, hashing, diff engine, local store
  SchemaScope.SqlServer   catalog reader, connection handling
  SchemaScope.Api         minimal API + SSE, embeds the built UI
  SchemaScope.Shell       WebView2 window, single file exe entry point
  SchemaScope.Spike       Phase 0 measurement console
web/                      React + TypeScript + Vite + Tailwind + Monaco
```

Supports SQL Server 2012 and later, Azure SQL Database, Azure SQL Managed
Instance and AWS RDS. The catalog queries adjust to the server version.

---

## Not in this version

Deployment scripting. Comparison is a solvable problem; generating a correct
deploy script for every edge case - dependency order, drop and recreate, data
preservation - is a much bigger one. Shipping half of it would be worse than
not shipping it. Export the objects as `.sql` and review them yourself.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the build, the dev loop and where
tests are expected. Found a security issue? [SECURITY.md](SECURITY.md) - please
report it privately rather than in an issue.

## License

[Apache-2.0](LICENSE). Built by [Shadowmark](https://shadowmark.in).
