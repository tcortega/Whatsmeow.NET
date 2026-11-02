# Whatsmeow.Sidecar.Native

This package holds `whatsmeow-net`, the sidecar binary that `Whatsmeow.Client` starts, for the two runtime identifiers of one operating system.
`Whatsmeow.Client` depends on it at exactly its own version, so you never reference it yourself.

## GPL-3.0 object code

`whatsmeow-net` statically links `go.mau.fi/libsignal`, which is licensed under GPL-3.0-or-later.
So each binary in this package is GPL-3.0 object code: one combined work, conveyed under GPL-3.0-only as a whole [inferred: GPLv3 §5(c) and §14, ADR 0016].

- `COPYING` is the GPL-3.0 text.
- `NOTICE` says what each part of Whatsmeow.NET is licensed under, and where the source is.
- `THIRD-PARTY-NOTICES.txt` holds the licence file of every module linked into `whatsmeow-net`, and of the Go standard library, verbatim.

The .NET packages (`Whatsmeow.Client`, `Whatsmeow.Protocol` and `Whatsmeow.Protocol.WhatsApp`) are MPL-2.0.
They talk to `whatsmeow-net` only through gRPC over a local socket, so the GPL does not reach them [inferred: FSF FAQ #MereAggregation, ADR 0016].

## Where the source is

The Corresponding Source of the binaries in this package is `whatsmeow-net_<version>_source.tar.gz`, attached to the GitHub release whose tag is `v` followed by this package's version, at https://github.com/tcortega/Whatsmeow.NET/releases.
It holds the whole tagged repository plus the `go mod vendor` output for `sidecar/`, so it builds without a network; `NOTICE` gives the command.

## What redistributing it owes

- Running `whatsmeow-net` on your own machines or servers owes nothing under the GPL [inferred: GPLv3 §0].
- Redistributing it, for example in an installer, a desktop app or a container image, conveys it, and you take on GPLv3 §6 for `whatsmeow-net` [inferred].
- Shipping it in a consumer device also owes Installation Information [inferred: GPLv3 §6 "User Product"].
- Whether an app can leave this package's assets out (for example with NuGet `ExcludeAssets`) and run an external `whatsmeow-net` is [unknown].
- Legal review of all of the above is [unknown]: this is the project's reading of the licences, not legal advice.
