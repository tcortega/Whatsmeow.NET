# Licensing

This is the project's reading of the licences, not legal advice; a legal review is [unknown] and still open ([ADR 0016](../adr/0016-sidecar-binary-gpl-3-corresponding-source-per-release.md), [ADR 0029](../adr/0029-gpl-release-obligations.md)).

## Two licences

* The .NET packages, `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting`, `Whatsmeow.Testing`, `Whatsmeow.Protocol` and `Whatsmeow.Protocol.WhatsApp`, and this repository, are MPL-2.0.
* The `whatsmeow-net` binary in `Whatsmeow.Sidecar.Native` statically links `go.mau.fi/libsignal`, which is GPL-3.0-or-later, so the binary is one combined work conveyed under GPL-3.0-only [inferred: GPLv3 §5(c) and §14, ADR 0016].

The .NET assemblies talk to `whatsmeow-net` only through gRPC over a local socket, as separate programs, so the GPL does not reach them or your app [inferred: FSF FAQ #MereAggregation, ADR 0016].
MPL-2.0 asks you to share changes you make to the library's own files, not your code [inferred: MPL-2.0 §3].

## What using the sidecar owes

* Running `whatsmeow-net` on your own machines or servers, a SaaS included, owes nothing under the GPL [inferred: GPLv3 §0].
* Redistributing it, in an installer, a desktop app or a container image you give to others, conveys it, and you take on GPLv3 §6 for `whatsmeow-net` [inferred]: pass on its licence and notices, and offer its Corresponding Source.
* Shipping it in a consumer device also owes Installation Information [inferred: GPLv3 §6 "User Product"].

The native package already carries what you need to pass on: `COPYING` (the GPL-3.0 text), `NOTICE`, and `THIRD-PARTY-NOTICES.txt` with the licence of every module the binary links.
Each GitHub release attaches the Corresponding Source, `whatsmeow-net_<version>_source.tar.gz`: the whole tagged repository plus the vendored Go modules, which builds without a network, as `NOTICE` explains.
Releases are never deleted, so the source stays where the packages point.

## Shipping no binary

An app can leave the native package out (`ExcludeAssets="all"` on `Whatsmeow.Sidecar.Native`) and use [External mode](external-mode.md) with a `whatsmeow-net` it runs separately, so the app itself carries no GPL binary [observed: `build/ci.sh install`, ADR 0046].
Whoever distributes the sidecar then, a container image of it for instance, takes on the obligations above.
