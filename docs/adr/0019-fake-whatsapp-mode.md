# 0019. Fake WhatsApp mode: a tagged fake client and a test-only control service

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #5, its plan review, and the tests named below

## Context

[ADR 0011](0011-integration-tests-by-default.md) makes a FullCycle against a real `whatsmeow-net --fake-whatsapp` the default test of every slice, but leaves open what the fake replaces, how tests drive it, and how it is kept out of what ships.
Issue #5 delivers the mechanism before any slice talks to whatsmeow; each later slice adds the controls it needs.
A Go file behind a build tag is invisible to build, test, lint and the cgo check unless the tag is passed [observed: #2 review; measured again for #5 with Go 1.27.3 and golangci-lint 2.15.0].

## Decision

- **What is faked.** Only whatsmeow's network side, `*whatsmeow.Client`, is replaced.
  `sidecar/internal/fakewhatsapp.Client` copies whatsmeow's method signatures at the pin, so the small interface a slice declares (docs/guidelines/go.md) is satisfied by both, and `cmd/whatsmeow-net` hands the slice one or the other.
  The store (whatsmeow's sqlstore, the outbox) stays real.
  Dispatching an event follows whatsmeow's `dispatchEvent` at the pin: handlers in order, the first `false` stops them and withholds the ack, and a panic is recovered and counts as success (`client.go:999-1013`).
  An ack is logged with the stanza's class and id, the attributes `sendAck` sends (`receipt.go:151-156`).
- **Build tag.** The fake package and its wiring (`cmd/whatsmeow-net/fake_on.go`) are behind `fakewhatsapp`.
  `tools/check-repo` allows the tag only in `sidecar/internal/fakewhatsapp/` and `sidecar/cmd/whatsmeow-net/`, so a slice is the same code in the test build and the release build.
  `build/ci.sh go` runs the sidecar's build, test, lint and cgo check once without the tag and once with it.
- **The fake never ships.** A release build defines `--fake-whatsapp` and refuses it with exit code 2 before it reads the token.
  `build/ci.sh go` fails when `whatsmeow-net` built without the tag links `internal/fakewhatsapp` or the generated fake package, and `build/ci.sh dotnet` fails when an assembly under `dotnet/src` contains the fake control's type.
- **Control service.** `proto/whatsmeow/fake/v1/fake.proto`, package `whatsmeow.fake.v1`, is outside `whatsmeow.v1`.
  It is served on the same gRPC server as the contract, so the same socket, token check and shutdown gate apply.
  It adds no `ErrorReason`; its errors arrive as INTERNAL with a message.
  It lives in the existing `proto` buf module (a new module would fail `buf breaking` against a base with fewer modules, proto.md), is linted, and is in `breaking.ignore`, so it may change freely.
  Its C# is generated only into `dotnet/tests/Whatsmeow.IntegrationTests/Generated/` (`types` / `exclude_types` in `buf.gen.yaml`).
- **Observing by condition.** `WaitForSent(session_id, after)` returns the session's first entry after a cursor and waits until there is one; only the call's deadline or cancellation ends it.
  A withheld ack is shown by order (the next stanza's ack comes first), never by waiting for an absence.
- **Shared fixture.** `FakeSidecar`, an xUnit v3 assembly fixture, builds the fake-tag binary and owns one sidecar for the run.
  It hands tests the public `WhatsmeowSidecar` and the control client, which goes through the sidecar's own channel (`WhatsmeowSidecar.CallInvoker`, internal), so it carries the current token and follows restarts.
  A test crashes the sidecar only with `CrashAsync`, which returns once the supervisor restarted it, in a collection that never runs in parallel.

## Consequences

- #5 proves the script, observe and ack path in Go, through the real `server.Server` over bufconn; the first .NET test that sees an ack comes with the outbox (#8).
- The "real SQLite file" of the shared fixture arrives with the store and its `--database` flag in #6.
- The fake's server-side state (its log, its clients) lives in the sidecar process and dies with it, so after a kill it cannot redeliver what was never acked, as WhatsApp would [inferred]. #8's kill tests must settle how the fake models that.
- Importing whatsmeow's root package for the client's types adds modules to `sidecar/go.mod` that the release build does not link yet; `tools/check-licenses` audits only what the release build links.
- Two internal seams in `Whatsmeow.Client` exist for the integration tests: `StartFakeWhatsAppAsync` and `CallInvoker`.
