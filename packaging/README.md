# Packaging

Manifests for the Windows package managers. Both exist so people can install
SchemaScope without downloading an executable from a browser, which is what
triggers the SmartScreen "unknown publisher" prompt - the warning comes from
the Mark of the Web that browsers attach to downloads, and package managers do
not go through it.

These are kept here rather than only in the upstream repositories so the
version, URL and hash live next to the release that produced them.

## winget

Three-file manifest under `winget/`. It points at the **installer**
(`SchemaScope-Setup.exe`).

To publish a version, open a PR against
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) placing these
files at:

```
manifests/s/Shadowmark/SchemaScope/<version>/
```

Validate and test locally first:

```powershell
winget validate --manifest packaging\winget
winget install --manifest packaging\winget
```

`wingetcreate update Shadowmark.SchemaScope --version <version> --urls <url>`
handles the bump and the PR in one step once the package is accepted.

### Per release

`InstallerUrl`, `InstallerSha256`, `PackageVersion` (in all three files) and
`ReleaseDate` all change. The URL must be the per-tag download link, never
`releases/latest/download` - winget pins a hash to a version, and the latest
permalink serves different bytes over time.

## Scoop

Single manifest at `scoop/schemascope.json`, pointing at the **portable**
`SchemaScope.exe`, which is what Scoop expects.

No bucket or review process is needed to use it:

```powershell
scoop install https://raw.githubusercontent.com/Shrikant-satpute/schemascope/main/packaging/scoop/schemascope.json
```

`checkver` and `autoupdate` are wired to the GitHub releases, so
`scoop update` picks up new versions and reads the hash straight from the
published `SHA256SUMS.txt`.

Submitting to the community `extras` bucket is possible later; it has its own
popularity criteria, and the raw URL above works in the meantime.
