# Contributing

## Building

You need the **.NET 9 SDK** and **Node 20+**.

```powershell
.\build.ps1              # front end + single file exe -> dist\SchemaScope.exe
.\build.ps1 -SkipWeb     # C# only, reusing the last front end build
```

## Working on the front end

```powershell
# terminal 1 - the API on a fixed port
dotnet run --project src\SchemaScope.Shell -- --server --port 5199

# terminal 2 - Vite with hot reload, proxying /api to 5199
cd web
npm run dev
```

`--server` mode uses a fixed API token that the Vite proxy injects on every
forwarded request. The packaged app mints a random one instead and hands it to
its own window. If you add a call that bypasses `web/src/api.ts`, remember it
needs the `X-SchemaScope-Token` header, or `?k=` for anything using
`EventSource`.

## Tests

```powershell
dotnet test
```

`tests/SchemaScope.Tests` covers the parts where a quiet mistake would be
expensive:

- **the normalizer**, because two objects that hash alike are never compared
  again - a wrong hash silently hides a real schema difference
- **the compare engine**, because "unreadable" must never be reported as
  "different"
- **the security fixes**, because those are the ones worth attacking

Anything touching hashing, normalization or the API's front door needs a test.

## Style

Match what is already there. The codebase leans on a few habits:

- Comments explain **why**, not what. If a line needs a comment to say what it
  does, rename something instead.
- Cheap checks before expensive ones. The compare is fast because it hashes
  first and only parses what is left - keep that shape.
- Nothing is ever hidden from the user. An ignore rule downgrades an object to
  *Formatting*; it never makes it disappear.

## Adding a normalizer rule

The Phase 0 spike reports every object that differs *only* by formatting, which
is the fastest way to find a rule worth adding:

```powershell
dotnet run --project src\SchemaScope.Spike -c Release -- `
  -s "Server=localhost;Database=Dev;Integrated Security=true" `
  -t "Server=localhost;Database=Prod;Integrated Security=true"
```

Each one it lists is a candidate. Add a test alongside the rule.

## Pull requests

Keep them focused - one change per PR. Say what problem it solves and how you
verified it. If it changes behaviour anyone could see, update the README in the
same PR.

By contributing you agree your work is licensed under Apache-2.0, as the project
is.
