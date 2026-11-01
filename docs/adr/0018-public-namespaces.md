# 0018. Public namespaces

- Status: Accepted
- Date: 2026-11-01
- Evidence: PR #26 review round 1; ARCHITECTURE Naming

## Context

The ARCHITECTURE Naming table puts `WhatsmeowSidecar` and `WhatsmeowException` under the C# root `Whatsmeow`.
The first slice put its public types in `Whatsmeow.Sidecar` and `Whatsmeow.Core`, because the house `.editorconfig` makes namespaces follow folders (`dotnet_style_namespace_match_folder`, an error in CI) and the slice and core folders are `Sidecar/` and `Core/`.
A consumer would then need two or more `using` lines for the entry point and its exceptions.

## Decision

- The entry point and the public types shared across slices are in namespace `Whatsmeow`, in files at the `Whatsmeow.Client` project root: `WhatsmeowSidecar`, `WhatsmeowSidecarOptions`, `SidecarInfo`, `SidecarTransport`, `WhatsmeowException` and its subtypes, and later `Jid`, the ids and `IWhatsAppClient`.
- Public records used by one slice only are in `Whatsmeow.<Slice>`, in `<Slice>/`.
- Internals stay in `Core/<Area>/` and `<Slice>/`, in the namespaces of their folders.
- Folders and namespaces still match, so no rule is switched off.

## Consequences

- `using Whatsmeow;` is enough to start a sidecar and catch its errors.
- The project root holds a growing set of shared public files; a type moves there only when a second slice uses it.
