# Code signing policy

This document describes who can release signed SchemaScope binaries, how those
binaries are produced, and what data the signing process handles. It exists
because a signature is only worth as much as the process behind it: the point
of publishing this is that you do not have to take the signature on trust.

## What is signed

Two files, both produced by the release workflow from a tagged commit:

| File | What it is |
| --- | --- |
| `SchemaScope.exe` | Portable single-file build |
| `SchemaScope-Setup.exe` | Per-user installer, which contains the same signed `SchemaScope.exe` |

The application binary is signed *before* it is packaged into the installer, so
the executable that ends up on disk after installing carries a signature too -
not just the installer that put it there.

Nothing is signed outside that workflow. There is no local signing step, and no
maintainer holds a signing key.

## Who can release

SchemaScope is currently maintained by one person, Shrikant Satpute
([@Shrikant-satpute](https://github.com/Shrikant-satpute)). The SignPath roles
therefore all resolve to that single maintainer:

- **Author** - may modify source code and open pull requests.
- **Reviewer** - may approve pull requests into `main`.
- **Approver** - may approve a signing request, and so cut a signed release.

This concentration is a real limitation and is stated rather than hidden. It
means the process protects against a compromised build environment or a
tampered artifact, but not against a compromised maintainer account. Two
controls narrow that:

- Multi-factor authentication is required on both the GitHub account and the
  SignPath account.
- Signing requests originate only from the `Release` workflow in this
  repository, and SignPath verifies that origin independently of the
  maintainer.

If additional maintainers join, this document is updated before they are
granted any signing role.

## How a release is built

1. A release is cut from a tagged commit in this repository.
2. GitHub Actions builds the web UI, runs the test suite, publishes the
   single-file executable, and packages the installer with Inno Setup.
3. Each binary is uploaded to SignPath directly from that workflow run.
   SignPath checks the request came from the expected repository, workflow and
   branch before it will sign anything.
4. The signed binaries come back into the same run, and only then are SHA-256
   checksums calculated. `SHA256SUMS.txt` therefore describes the exact bytes
   published on the release page.

Every input is public: the source, the build definition and the workflow logs.
You can rebuild from the same tag and compare everything except the signature
itself.

## Privacy

The signing process handles no personal data belonging to users of
SchemaScope. SignPath receives the build artifacts and the metadata of the
GitHub Actions run that produced them - repository, workflow, commit, and the
identity of the maintainer who approved the request. It receives nothing from
anyone who downloads or runs the application.

SchemaScope itself sends no telemetry, and database credentials never leave the
machine the application runs on. See [SECURITY.md](SECURITY.md) for how stored
credentials are handled.

## Reporting a problem

If you believe a signed SchemaScope binary was not produced by this process, or
you find a signature that does not verify, please report it privately using the
process in [SECURITY.md](SECURITY.md) rather than opening a public issue.

## Acknowledgement

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).
