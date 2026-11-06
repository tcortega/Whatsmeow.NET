# Tests

Read this before writing or touching a test.
This is the house tests guide (bridge, compras, chrominal) re-targeted from an HTTP API to a library plus sidecar.
The reference tests are named in `AGENTS.md` (Gold-standard slice).
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

* dispatcher per-chat ordering and the ack watermark, with the lane of every event type (`Events/EventLaneTests.cs`, ADR 0036)
* the supervisor restart/backoff state machine (driven by `FakeTimeProvider`)
* the transport's request body fence (`Core/Transport/RequestBodyFence.cs`, #71), the one exception outside those two.
  It works around a race between grpc-dotnet and SocketsHttpHandler that the refused-upload test hit in 7 of 500 runs under load, so integration cannot pin its contract.
  `Transport/RequestBodyFenceTests.cs` drives it with a stream whose write blocks on the test, so every step is ordered.
  The refused-upload FullCycle in `Media/MediaTests.cs` stays the end-to-end check.

Fixture-free methods live in `tests/Whatsmeow.IntegrationTests` because no sidecar path reaches what they check:

* `Support/LogRedactionTests.cs`, the live log's redaction, which no live run can check for itself.
* `Support/LogCaptureTests.cs`, the log wait's single visitation of each record, cancellation, and selection of the requested lifecycle record (#137).
* `Events/JidParseTests.cs`, `Jid.Parse` throwing without the text it refused (ADR 0039), and a JID the sidecar sent failing as `Internal` without it (ARCHITECTURE §8.7): the sidecar records no event with a JID the client cannot parse (#122), and no response found at the pin sends one with a text.
  An empty JID does reach a response: `GetGroupInfo` sends a member without a server empty, which `Groups/GroupTests.cs` uses for the sidecar's side.
  For an event, `Events/EventTests.cs` has the fake record a message with no chat as it is (`RecordEvent`), so the client's skip and ack show.

Proto-to-record mapping and error mapping are covered through integration, not unit tests.
Go keeps per-slice unit tests; see [go.md](go.md).

Two valid shapes: a FullCycle for a simple slice, and one method per behavior when TDD or a distinct rule needs its own red/green.
Do not write a test to raise a coverage number.
Line coverage is a diagnostic. Behavior coverage is the point.

## Fixture

One shared sidecar fixture: `FakeSidecar`, an xUnit v3 assembly fixture (`dotnet/tests/Whatsmeow.IntegrationTests/FakeSidecar.cs`, ADR 0019).
It builds `whatsmeow-net` with `-tags fakewhatsapp` and owns one `--fake-whatsapp` process for the run.
Tests get the public client from `fixture.Sidecar` and talk only the public API.
They script and observe the fake WhatsApp through `fixture.Fake`, the test-only `whatsmeow.fake.v1` client on the sidecar's own channel.
Observe with `WaitForSent`, which waits for a condition; show a withheld receipt by order (the next message's receipt comes first), never by waiting.
The sidecar is shared and tests run in parallel, so each test uses session ids of its own.
A test that needs a paired session links it with the fake's `LinkDevice` without a Pair call, the quick way; `Sessions/SessionSetup.cs` has the helpers, and `Support/StateWatch.cs` reads a state watch by condition.
The Pairing tests pair through the public API, with the fake WhatsApp as the phone (`Pairing/PairingTests.cs`).
A test that starts a sidecar of its own gives it a database of its own (`binaries.NewDatabase()`): one sidecar owns a database file.
A test that crashes the sidecar calls `fixture.CrashAsync` and sits in the `SidecarCrashes` collection, which never runs in parallel; `CrashAsync` throws anywhere else.
`CrashAsync` returns once the supervisor restarted the sidecar, and fails the test at once with the reason when a restart attempt fails, since the next attempt is a backoff away.
Until the sidecar is back, any other test that reaches for `fixture.Sidecar` or `fixture.Fake` fails with the crash, and with the last attempt's reason once one failed, not with a refused socket (#77).
The fixture writes the client's and the sidecar's records to `dotnet/artifacts/TestResults/fake-sidecar.log` as they come, and the CI dotnet job uploads that file when it fails.
Do not start a sidecar per test; the Sidecar slice's start and stop tests are the exception, because starting and stopping is their behaviour, and so are the delivery tests that stop or kill the .NET side (`Events/EventProcessTests.cs`): they start fake-mode sidecars of their own (`fixture.Binary`, `binaries.NewDatabase()`), and kill the .NET side as a child process, `SidecarHost events`.
So do the hosting tests (`Hosting/HostingTests.cs`): the generic host starting and stopping the sidecar is their behaviour, so each host gets a fake-mode sidecar of its own the same way.
So do the sample's tests (`Samples/EchoBotTests.cs`): the sample's own setup in a host that starts its sidecar, and the redelivery test two hosts on one database ([ADR 0049](../adr/0049-the-hosted-sample-and-its-fake-mode-check.md)).
So does the send pacing test (`Messaging/SendPacingTests.cs`): the pacing options apply when the sidecar starts, so it starts a fake-mode sidecar of its own, with an hour between sends, a wait it shows without waiting.
So do the External-mode tests (`Sidecar/ExternalTests.cs`): what happens to a sidecar the client did not start is their behaviour, so each runs one standalone (`Support/StandaloneSidecar.cs`) and kills it by its own process.
`build/ci.sh external` runs the whole suite once more with `WHATSMEOW_NET_TEST_SIDECAR=external`: the fixture then runs its sidecar standalone over TCP and connects in External mode, and `CrashAsync` kills and starts that process itself (ADR 0046). The fixture is the only reader of that variable.
So is the media transfer per transport (`Media/MediaTransferTests.cs`): it runs over the named pipe too, and measures the sidecar's memory alone, in the `MediaTransfers` collection, which never runs in parallel.
So are the meter source tests (`Telemetry/MeterTests.cs`): where the meter comes from is decided at the start.
So are the fresh companion's tests (`Support/FreshCompanionTests.cs`): a database the run creates for one session is their behaviour (ADR 0060).
Metrics on the shared sidecar are read with a `MetricCollector` on `fixture.MeterFactory`, which sees that sidecar only, and told apart by the `whatsmeow.session.id` tag.
A test that listens to spans, or reads a count no session id tells apart (`whatsmeow.errors`), sits in the `TelemetryListeners` collection, which never runs in parallel: an `ActivityListener` is process-wide ([ADR 0037](../adr/0037-traces-and-metrics.md)).
The shared sidecar has a media root (`fixture.MediaRoot`); a test that hands files over puts them under a directory of its own there.
Do not mock the transport, the dispatcher, or the sidecar.

The consumer fake, `FakeWhatsAppClient`, has tests of its own in `tests/Whatsmeow.Testing.Tests`, with no sidecar (ADR 0036).
They use only its public API, as a consumer would: no project gives that one internals, so the compiler holds the rule.

xUnit v3 on Microsoft.Testing.Platform, not TUnit.
A test project with no source file yet may report zero tests; once it has a `*.cs` file, zero discovered tests fails the run, and a project named `*Tests` that is not a platform app fails the build (`dotnet/Directory.Build.targets`).
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

The delivery invariants (ARCHITECTURE §13, 1-3) get kill-either-process tests: kill the sidecar or the .NET side mid-stream, restart, and assert that no event is lost at the handler.
Delivery is at-least-once (ARCHITECTURE §8.4), so a repeat is allowed: assert that it only follows the last acked cursor and carries the same `MessageId` and `SessionId`.
Wait on a condition (the event arrived, the cursor moved), never a duration.
To fix when an ack goes out, give the sidecar a `FakeTimeProvider` (`StartFakeWhatsAppAsync`'s internal overload): its timer acks only when the test moves the clock.

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
State the mutation in the PR body.

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
* overriding `Equals`, `==`, or a comparer on a public record so assertions pass (record structural equality is required, not a cheat). The allowed case: a record holding `ImmutableArray` may define element-wise equality, because that is its structural equality (`MediaRef`, ADR 0026; `GroupInfo`, `CreatedGroup`, `GroupInfoChanged` and `ReceiptReceived`, ADR 0030; the media metadata records, ADR 0033; `HistoryReceived`, ADR 0035; `SendOptions` and `MessageReceived`, ADR 0038); it compares every field, so a test compares the whole record. The one field left out is `MessageReceived.Raw`, the experimental hatch, which a test compares on its own (ADR 0038)
* `try/catch` or `if` around an assert so failure is swallowed
* new `Skip`, `Skip.If`, `Explicit`, commented-out facts, or `Assert.True(true)`. The declared exceptions:
  * the platform condition `NoexecTests` (the copy fallback), which needs a `noexec` mount that only root can make: its tests run where `WHATSMEOW_NET_TEST_NOEXEC_DIR` names one (`build/ci.sh` provides it on linux-x64 CI), and fails instead of skipping when `CI=true` on linux-x64 and the mount is missing;
  * the live opt-in `[LiveFact]`, explicit unless `WHATSMEOW_LIVE_DATABASE` is set (and, for the phone-code test, `WHATSMEOW_LIVE_PHONE_NUMBER`), in `Whatsmeow.LiveTests` only: `build/ci.sh` fails when `LiveFact` appears anywhere else, `Explicit` outside `LiveFactAttribute.cs`, or a `[Fact]` or `[Theory]` in `Whatsmeow.LiveTests`
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
Time is faked, never waited on: one `FakeTimeProvider` drives now, delays and Polly ([csharp.md](csharp.md) Time).

## Live WhatsApp

The default suite and CI use `--fake-whatsapp`.

`tests/Whatsmeow.LiveTests` runs against real WhatsApp with a dedicated test number and manual pairing ([ADR 0021](../adr/0021-pairing.md)).
Its tests are `[LiveFact]`: they run only when `WHATSMEOW_LIVE_DATABASE` names the live database, and otherwise start nothing and are reported as not run.
A test that needs more says so, and is not run without it: `NeedsPhoneNumber` (the phone-code test) needs `WHATSMEOW_LIVE_PHONE_NUMBER`, the dedicated number; `NeedsPeer` needs `WHATSMEOW_LIVE_PEER_DATABASE`, the peer session's database; `NeedsOwner` needs `WHATSMEOW_LIVE_OWNER` set, the owner at the phone; `NeedsFreshPairing` needs `WHATSMEOW_LIVE_FRESH_DATABASE` and `WHATSMEOW_LIVE_HISTORY_BOUNDS`, two new files, `WHATSMEOW_LIVE_KNOWN_DEVICES`, the peer account's devices from root's preflight, and a QR scan by the peer account's phone every run (ADR 0060).
The fresh companion test also needs `WHATSMEOW_LIVE_HISTORY_DAYS_LIMIT`, the full sync days limit its sidecar asks for (#42), a whole number above zero in digits only; it is not an opt-in: when the test runs and it is missing or malformed, the test fails before it starts a sidecar or creates a file.
A test not run still leaves the run green, so check the run's summary: the `NeedsPeer` tests, the kill tests among them, must show as run, not skipped.
`build/ci.sh` runs `dotnet test` without any of them, so CI never runs them, and the live sidecar refuses to start without the database.
The live session `live` is persistent: it is paired by QR code once and reused by every later run, #10's included, with no scan; it is never logged out or deleted.
Its database (`~/.whatsmeow-net-live/live.db` in `~/secrets.env`, a 0700 directory outside the repository, the file 0600) holds the account's keys: a secret, never committed or used by CI, and copied only into the run's backup (below).
If the phone logs the session out, that run fails with `LoggedOut`, and the next run pairs it again by QR code.
The phone-code test pairs a throwaway session, `live-phone`, and logs it out and deletes it at the end, so the phone keeps only the live session's device.
The fresh companion test (`LiveFreshCompanionTests`, ADR 0060) pairs a new companion of the peer's account, `peer-fresh-<UTC time>`, on the new database it creates in a sidecar of its own, to check a business name and a push name the new store has not cached (read before each stimulus; a value cached already fails its branch) and the run's group photo, with `ContactChecks`, and #42's history bounds (`LiveHistoryBounds`).
The proof, the steps, the hold, the capture bound and the cleanup are `Support/FreshCompanion.cs`, which `Support/FreshCompanionTests.cs` checks in fake mode.
The business name and push name steps run as soon as the session is Connected, with its history held; the hold stops recording, not downloads, so no first sight is promised and no second scan follows.
It logs the session out and deletes it only once it proved the account, a new device and its own database, and only after its own logout read LoggedOut 0; otherwise the session and its keys stay. Both files stay: the database may hold the account's keys, contacts and history, and SQLite may keep deleted rows; delete them by hand.
The group photo is set and removed through the live tests' control, `whatsmeow.livetest.v1`, which only a `-tags livetest` sidecar serves.
About and the saved name are not run there, so it is not the Contacts FullCycle passing live.
Same assertions as the fake-mode suite: a live test calls the fake-mode tests' own checks (`Whatsmeow.IntegrationTests/Pairing/PairingChecks.cs`, through a project reference), and only the person replaces the fake. No live-only skips and no weakened equals.
Each slice keeps them in `<Slice>Checks.cs` beside its fake-mode tests (`MediaChecks`, `MessageChecks`, `GroupChecks`, `PresenceChecks`, `ContactChecks`, `ChatStateChecks`), and both modes compare the whole record they build.
What only WhatsApp sets (a message's source, push name and time, a group change's sender and time, an upload's URL) is passed in: the fake's are fixed, and a live test reads them off the event once it checked that they name the right account (`LiveChecks`).
A question WhatsApp alone answers, [unknown] in ARCHITECTURE §14, is written to the log before any check, so a failing check still leaves the measurement.
Each `live:` line starts with the UTC time, so §14 can date it, and names a JID only by its form (`LiveChecks.Form`); a live check's failure is redacted too (`LiveChecks.Equal`, `LiveChecks.Redacting`), since a record prints its JIDs.
Redaction (`LiveRedaction`) takes out every JID user part, the test number, and both accounts' phone numbers and LIDs wherever they appear, a mention's bare `@user` included; a wait's failure names what was awaited, never its text.
A live test reads only what is its own: the contacts test compares the peer's blocklist entries and never prints the account's other ones.
The number and the database path are secrets: keep them in `~/secrets.env` and never print them; the live sidecar log redacts the number.
Run, from the repository root, exporting only the variables the tests need (never source `~/secrets.env`; for the phone-code test, export `WHATSMEOW_LIVE_PHONE_NUMBER` the same way, and `WHATSMEOW_LIVE_OWNER=1` when the owner is at the phone):

```sh
eval "$(mise env -s bash)"
export WHATSMEOW_LIVE_DATABASE="$(grep '^WHATSMEOW_LIVE_DATABASE=' ~/secrets.env | cut -d= -f2-)"
export WHATSMEOW_LIVE_PEER_DATABASE="$(grep '^WHATSMEOW_LIVE_PEER_DATABASE=' ~/secrets.env | cut -d= -f2-)"
dotnet test --project dotnet/tests/Whatsmeow.LiveTests -c Release
```

The person watches `dotnet/artifacts/live/`: `qr.png` is the QR code to scan (rewritten on each rotation), only when the live or the peer session is not paired yet (the peer's with the second account's phone), and `pairing-code.txt` the code to enter on the phone; both are deleted at the end, and a QR payload is never written as text (it carries the device's ADV secret). `sidecar.log` is the sidecar's log, with every JID's user part and the test number redacted.
The QR test stays connected until the live session is `CaughtUp`, at most ten minutes, as every first connect of a run does (`LiveSidecar.CatchUpBound`: the peer, a busy account, took 4 min after a day offline), and prints how long that took: a device that went offline right after pairing stayed "connecting" on the phone (ADR 0021).

Every live test is in the `Live` collection: one after the other, on one `LiveSidecar` and one `LivePeer`.
The live sidecar is a release build with `-tags livetest`, the live tests' seams ([ADR 0032](../adr/0032-live-test-seams-and-the-peer-session.md)): a loose id rule, and a handler that withholds the first answer to a text starting `wmn-withhold `. CI proves no release binary links them.
The traffic is between two accounts: `peer`, a persistent session on the owner's second number, paired once by its own QR test like `live` and never logged out, deleted or re-paired, runs in a sidecar and on a database of its own; it sends the bursts to the live account and receives what the live session sends.
A session's own JID comes from `SessionInfo.Jid` (this device's JID; the account's chat is it with device 0).
Only the `NeedsOwner` tests need the owner: some wait for the owner's ping, `wmn ping` sent from the phone to "Message yourself", before the run or within five minutes of the first such test, a wait bounded apart from the test's own work.
The others ask the owner what a phone shows, the live account's or the peer's: the question goes to `owner-question.txt`, `sidecar.log` and the test output, and the owner answers with a text to "Message yourself" on the live account's phone that starts `wmn yes` or `wmn no`, with what they saw, within five minutes; the answer is logged, not checked.
Every live test puts back what it changed, after its body, whatever the body did (`LiveRestore`): each restore runs on its own 30 s bound, whatever another threw; a failed one goes to the log, and fails the test only when the body passed, so the body's failure stays the one reported.
What the API reads is set back to what it was: a group's name, topic, announce, lock, member add mode and roles, and the peer's membership.
What it cannot read is set to the default: a chat is unmuted and marked read, a disappearing timer (the chat's or the group's) turned off, the peer unblocked (before the block test too, as an aborted run leaves it), and both sessions' presence unavailable.
So the live tests assume the owner's own two test accounts, where those defaults are the state: never point them at accounts whose chats with each other are muted, unread, disappearing or blocked on purpose.
The run's one group (`LiveGroup`) has the peer as its only other member; at the end the live session removes the peer and leaves, the closest WhatsApp has to deleting it. No other number is ever added to a group.
The run's one group (`LiveGroup`) first closes the groups an aborted run left: the ones the live account owns, whose name is one a run gives or a test sets (`wmn live <time>`, with any ` crew` or ` not mine`), and whose only members are the two accounts.
The ref lifetime test keeps its refs, media keys included, in the file `WHATSMEOW_LIVE_MEDIA_REFS` names, outside the repository (it refuses a path inside; the file is kept 0600); without it, it tries this run's ref only.
The media size ladder sends 181 MiB of documents to the peer, so it runs only with `WHATSMEOW_LIVE_MEDIA_SIZES` set (`NeedsMediaSizes`).
The kill tests call the fake-mode kill tests' checks (`Whatsmeow.IntegrationTests/Events/DeliveryChecks.cs`); the .NET kill runs `SidecarHost events-live` on the live database, so the live sidecar stops first and starts again after it.
Every measurement goes to `sidecar.log` and the test output, redacted, for ARCHITECTURE §14; the peer's log goes to `peer.log`, and the .NET kill's host log to `host.log`.
A sidecar exit the client did not ask for adds a `live: UNEXPECTED SIDECAR EXIT` line with its exit code and UTC time, in the log and the test output; one outside a kill test's own kill means something else killed it, and the run's results around it are not evidence.
Each run backs up the live and peer databases before their first start, while no sidecar holds them (`StoreBackup`, under the store's lock): `~/.whatsmeow-net-live/backup/live-<yyyyMMddHHmmss>/` and `peer-<...>/`, the directories 0700 and the files 0600, the database with its `-wal` and `-shm` as they are.
These are the only copies of the keys; delete old ones by hand.
To restore one, stop every sidecar on that database and copy its files back.

Other fake-only seams (injected failures, scripted disconnects) stay on fake-mode fixtures. They are not a live concern.

## Bugs

Start with a failing E2E reproduction the way a user hits it: through the public API against a real sidecar.
No reproduction, no fix.

## Do not

* Fake a passing test.
* Call internal types directly when the public API reaches the behavior.
* Reset the store mid cycle unless the cycle needs a clean slate.
* Add FluentAssertions or a second assertion library.
* Chase coverage or mutation score as a number. Use mutation only as the "does this test actually die" check.
