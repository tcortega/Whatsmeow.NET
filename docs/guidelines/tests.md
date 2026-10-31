# Tests

Read this before writing or touching a test.
This is the house tests guide (bridge, compras, chrominal) re-targeted from an HTTP API to a library plus sidecar.
No reference test exists yet; the first Phase 1 FullCycle becomes it and is named in `AGENTS.md`.
The test layers and where they live are in ARCHITECTURE §11.

The filter comes from the research: a green suite is worthless if it still passes when the code is wrong.

## Prime directive

The spec (the issue, ARCHITECTURE, these guides, the user path) is the source of truth, not the suite.
If a test conflicts with the spec, stop.
Do not make it pass.
Do not silently edit the test.
Say what is blocked and wait.

## Default

Every slice gets one integration FullCycle through the public `IWhatsAppClient` against a real `whatsmeow-net` started with `--fake-whatsapp` and a real SQLite file.
That is `tests/Whatsmeow.IntegrationTests`.

.NET unit tests exist only for:

* dispatcher per-chat ordering and the ack watermark
* the supervisor restart/backoff state machine (driven by `FakeClock`)

Proto-to-record mapping and error mapping are covered through integration, not unit tests.
Go keeps per-slice unit tests; see [go.md](go.md).

Two valid shapes: a FullCycle for a simple slice, and one method per behavior when TDD or a distinct rule needs its own red/green.
Do not write a test to raise a coverage number.
Line coverage is a diagnostic. Behavior coverage is the point.

## Fixture

One shared sidecar fixture via `IClassFixture<T>` (or an xUnit v3 assembly fixture).
It owns the `whatsmeow-net --fake-whatsapp` process and its control service (inject events and failures).
Tests get a client from the fixture and talk only the public API.
Do not start a sidecar per test.
Do not mock the transport, the dispatcher, or the sidecar.

xUnit v3 on Microsoft.Testing.Platform, not TUnit.
Do not copy `ClassDataSource` or `[Test]`.

## FullCycle

Use this for a simple slice whose happy path is the feature.
Name it `FullCycle_...`.
Walk the real user path: pair, send, receive the event, ack, read back.

After each mutation, read back through the public API and assert the whole record.

```csharp
[Fact]
public async Task FullCycle_SendTextReceiveAck()
{
	var client = await fixture.GetClientAsync(TestContext.Current.CancellationToken);
	var sent = await client.Messages.SendTextAsync(chat, "hello", cancellationToken: TestContext.Current.CancellationToken);
	await ValidateEcho(fixture, sent);
}
```

Prefer `Assert.Equal` on the public record.
Prefer `with { }` to build the next expected from values you computed, not from rerunning the code and pasting output.
The Validate helper may only read and `Assert.Equal`.
It may not hide loops, branches, or swallowed failures.

Do not laundry-list fields.
Do not assert gRPC status codes or `RpcException` details; they never reach the public API.

## Delivery guarantees

The delivery invariants (ARCHITECTURE §13, 1-3) get kill-either-process tests: kill the sidecar or the .NET side mid-stream, restart, and assert no lost and no duplicated events at the handler.
Wait on a condition (the event arrived, the cursor moved), never a duration.

## One method per behavior

Use this when the slice is not simple, or when TDD needs a single rule red before the rest exists.
One user visible behavior per method.
The name says the behavior: `SendText_NotLoggedIn_Throws`, `Pair_QrExpired_EmitsNext`, `Stream_SidecarRestart_ResumesFromCursor`.

Same fixture, same public client, same boundary (process, socket, SQLite, events).
Same filter: the method must die when that behavior breaks.

Do not turn this into one method per API member (`SendText_Returns`).
That is a laundry list with extra names.
A `SessionNotFoundException` or validation test belongs here when it is a real product rule.

## Write it so it can fail

A new test is not accepted until it is seen failing for the right reason: an assertion, not a compile error, not a fixture typo.

Then mutate: break the production path the test claims to cover, rerun, confirm that test fails, revert the break.
If it still passes, delete the test.
State the mutation in the commit body.

This is the TestGen-LLM filter and the ACH point.
Raw tests are junk until they die when the code is wrong.

Holdout extra cases do not save a weak cycle.
If the happy path would stay green after a user-visible break, more error-path tests will not help.

## Existing tests are frozen during implementation

Do not modify, delete, skip, or weaken an existing test to make a slice pass.
Changing a test is its own task and its own diff.

A new test may ship in the same commit as the feature (we push slices atomically).
Write the test first, see it red, then implement.
Once it has gone red for the right reason, do not edit it to match a weaker implementation.
If the test looks wrong, stop and say so.

## Banned

Any of these in a diff is a failed slice, green or not:

* `Environment.Exit`, `Environment.FailFast`, or anything that kills the runner
* hooks or modules that patch xUnit results
* overriding `Equals`, `==`, or a comparer on a public record so assertions pass (record structural equality is required, not a cheat)
* `try/catch` or `if` around an assert so failure is swallowed
* new `Skip`, `Skip.If`, commented-out facts, or `Assert.True(true)`
* weakening `Assert.Equal(expected, actual)` to `NotNull` or dropping fields
* special-casing chats, ids, or payloads that exist only in the test
* retries or loops to get past a failure
* deriving expected values by running the code under test and pasting the output

## Determinism

A flaky test is a failing test.
Fix it or delete it on sight.

No `Thread.Sleep`, no `Task.Delay` to wait for something.
No public network in the default suite.
No order dependence between facts beyond what the shared fixture already owns.
Wait on a condition, not a duration.
Time comes from `FakeClock` wherever the code takes an `IClock`.

## Live WhatsApp

The default suite and CI use `--fake-whatsapp`.

`tests/Whatsmeow.LiveTests` runs against real WhatsApp with a dedicated test number and manual pairing.
It is opt-in through environment variables and never runs in default CI.
Same assertions as the fake-mode suite. No live-only skips and no weakened equals.

Fake-only seams (injected failures, scripted disconnects) stay on fake-mode fixtures. They are not a live concern.

## Bugs

Start with a failing E2E reproduction the way a user hits it: through the public API against a real sidecar.
No reproduction, no fix.

## Do not

* Fake a passing test.
* Call internal types directly when the public API reaches the behavior.
* Reset the store mid cycle unless the cycle needs a clean slate.
* Add FluentAssertions or a second assertion library.
* Chase coverage or mutation score as a number. Use mutation only as the "does this test actually die" check.
