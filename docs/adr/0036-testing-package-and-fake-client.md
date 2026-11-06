# 0036. Testing: `FakeWhatsAppClient` on the client's own event pipeline, one lane function, and fakes in a host

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #41, its plan review and the review of PR #79; `Whatsmeow.Testing.Tests` (`EventTests`, `CallTests`, `HostingTests`, `ExceptionTests`); `Whatsmeow.Client.Tests/Events/EventLaneTests.cs`; whatsmeow's `types.JID.String` at the pin (types/jid.go:218-230) and the sidecar's event mappings, which write every lane JID with it
- Amended by: [ADR 0055](0055-public-api-baseline-and-package-validation.md) (a member added to `IWhatsAppClient` after 1.0 takes a CP0006 suppression, still without a default implementation)
- Amends: [ADR 0012](0012-iwhatsappclient-interface-exception.md) (`Raise(event)` is `RaiseAsync`); [ADR 0034](0034-hosting-package.md) (what the host does for a replaced key); ARCHITECTURE §8.1 and §8.4

## Context

Applications built on the library test their own handlers and replies without a sidecar or a phone.
ADR 0012 keeps `IWhatsAppClient` as the one public interface for that, and `Whatsmeow.Testing` ships `FakeWhatsAppClient` as a product.
A fake that dispatched events its own way would let a consumer's test pass on an ordering or an error rule the real client does not have.
ADR 0034 left open what a host does when a test replaces a declared session's keyed client: the handlers were attached only to the sidecar's own client, and the real session was still connected.
What the code and the measurements showed:

- `SessionEvents` held the handlers, `WaitForAsync`, `OnHandlerError` and the two dispatchers together with the gRPC readers [observed: `Events/SessionEvents.cs` at edb40b1].
- The dispatcher's lane came from the proto field, and the records carry the same JID [observed: `EventMapping.Lane` at edb40b1].
  The sidecar writes every lane JID with whatsmeow's `JID.String()` [observed: `internal/events/mapping.go`, `messaging/receipt.go`, `contacts/event.go`, `groups/event.go`, `chatstate/event.go`, `media/retry.go`, `events/ephemeral.go`], and `Jid.ToString()` writes the same text for the same JID [observed: `Jid.cs`, types/jid.go:218-230; `EventLaneTests`].
- A library exception a consumer builds had `ErrorCode` `Unknown`, `Code` 0 and `Until` null, since their setters were `internal init` [observed: `WhatsmeowException.cs`, `WhatsAppErrorException.cs`, `TemporarilyBannedException.cs` at edb40b1]; the base-type reasons (`INVITE_LINK_INVALID`, `WHATSAPP_TIMED_OUT`, `NO_PUSH_NAME`) are told apart only by `ErrorCode`.
- A host with only `Whatsmeow:SidecarPath` set, to a file that does not exist, builds its unstarted `WhatsmeowSidecar` and passes `ValidateOnStart` [observed: `HostingTests`, on `Host.CreateEmptyApplicationBuilder`].
- A container does not dispose an instance it was given: a fake registered with `AddKeyedSingleton(key, instance)` still takes events after the host is disposed [observed: `HostStopped_ItsHandlersComeOffTheFake_WhichTheTestStillOwns`].
- Implementing the `[Experimental("WMN1001")]` `SendRawAsync` raises no WMN1001 in the implementing assembly [observed: the Testing project built without the attribute on the fake's member, reporting only the public API change].
- A raise that waited to write when the fake was disposed waited forever: its semaphore and channel waits took only the caller's token, and the stopped dispatcher reads nothing more [observed: `Disposed_WhileRaisesWaitBehindABlockedHandler_EveryRaiseEndsObjectDisposed`, 132 raises on one chat, at 7d5538f].
- A handler that awaited the raise of an event of its own chat waited forever, behind itself [observed: `EventOfItsOwnChat_RaisedFromAHandler_FailsInsteadOfWaitingForever_WhileOtherLanesRun` at 7d5538f].
- A host whose handlers went on the fakes in `StartingAsync` kept them there when a later hosted service failed its `StartAsync`, since a failed start calls no stop method (ADR 0034), so the fake's events then reached a disposed container [observed: `LaterServiceFailsToStart_NoHandlerIsLeftOnTheFake` at 7d5538f].

## Decision

- **`FakeWhatsAppClient`**, in namespace `Whatsmeow.Testing`, is `IWhatsAppClient` and `IAsyncDisposable`, made with `(SessionId, TimeProvider? = null)`.
  `RaiseAsync(event, cancellationToken)` returns once every handler of the event finished; a handler that throws is reported on `OnHandlerError` and does not fail it, as on a real session. An event of another session throws `ArgumentException`.
  A raise into the lane of the handler it is called from throws `InvalidOperationException` instead of waiting behind that handler forever; the lane is known from an `AsyncLocal` the raised work sets, and work a handler started that outlives it raises freely.
  Handlers that wait for each other's raises across lanes still wait forever, and so do raises from the handlers of `MaxConcurrentLanes` (16) lanes at once; the docs of `RaiseAsync` say so.
  A raise waits for the semaphore, the channel and the handlers with the pipeline's stop linked in, so a dispose ends every waiting raise with `ObjectDisposedException`.
  `Calls` is every call made, in order, as `FakeCall { Member, Arguments, Timestamp }`: the member as `nameof` names it, the arguments by parameter name without the token, collections copied and an uploaded stream as its bytes. The event members (`On`, `WaitForAsync`, `OnHandlerError`) are not calls.
  Two `FakeCall`s are equal when their member, time and arguments are, each argument by its own `Equals`, so a test compares a whole call with one it builds; an `ImmutableArray` argument equals only itself, so a collection is compared on its own.
  `FailNext(member, exception)` makes the next call of a member throw that exception, one per call in the order given; the call is still recorded, and a stream throws when it is first read. A member that is not recorded, such as `On` or `RaiseAsync`, throws `ArgumentException`, so a typo never queues a failure that never comes.
  Every other member returns a plausible result at once, on the fake's `TimeProvider`, with numbers of its own for what it makes (groups, invite links, media, pairing codes); it enforces no WhatsApp rule and paces nothing. A call whose token is already cancelled throws and is not recorded, and so does every call once the fake is disposed, with `ObjectDisposedException`, as the client's calls do once its sidecar is disposed.
  An upload of a file is recorded, and takes its `FailNext`, before the file is read, so a missing file is still a call. The pairing stream stops on the token given through `WithCancellation` too.
  The fake's `SendRawAsync` carries `[Experimental("WMN1001")]` too, so a direct call on the fake is flagged as one through the interface is.
- **One event pipeline.** `Events/EventPipeline` in `Whatsmeow.Client` holds the handlers, `WaitForAsync`, `OnHandlerError` and the durable and ephemeral dispatchers; `SessionEvents` keeps the readers and the ack and feeds it, and the fake feeds it through `InternalsVisibleTo`, with no ack.
  So per-chat order, chats running concurrently, ephemeral lanes apart, and the handler error rules are the client's own code, not a copy.
- **One lane function.** `EventMapping.Lane(WhatsAppEvent?)` gives the lane from the record, for the client and the fake alike, and `EventMapping.IsEphemeral` says which dispatcher an event goes to; the two proto-based lane functions are gone.
  The key is the JID's text, which is the proto field's text (Context), so the real dispatcher's keys do not change. `EventLaneTests` checks that for a sample of every payload case, and fails when a new payload case has no sample.
  One deliberate change: an event this client cannot map runs in the session's lane instead of its chat's. It runs no handler, so it orders nothing; it is only acked in its turn.
- **Library exceptions can be built.** Each subtype of one reason (`RecipientNotFoundException`, `RateLimitedException`, `MediaExpiredException`, `NotLoggedInException`, `SessionLoggedOutException`, `SessionNotFoundException`, `OutcomeUnknownException`, `WhatsmeowValidationException`, `WhatsAppErrorException`, `TemporarilyBannedException`) sets its `ErrorCode` in every public constructor, so `new RecipientNotFoundException("...")` carries `RecipientNotOnWhatsApp` as the library's does; the library's own throws use the same constructors.
  `WhatsmeowValidationException` so carries `InvalidArgument` for a local invalid value too, where it carried `Unknown`.
  `WhatsmeowException.ErrorCode`, `WhatsAppErrorException.Code` and `TemporarilyBannedException.Until` have public `init`, so a test sets what a constructor cannot know, or overrides the code, and `FailNext` throws it.
  The subtypes of several reasons (`NotConnectedException`, `PairingException`, `SidecarUnavailableException`) and the base type take the code from the test.
- **Fakes in a host.** `builder.UseFakeClient(fake)` (`FakeWhatsAppHostingExtensions`) declares the fake's session, makes the fake its keyed `IWhatsAppClient` (the earlier keyed registrations removed), and registers the internal `ReplacedSession`; it works before or after the application's own registrations.
  A host with a `ReplacedSession` starts no sidecar, and creates and connects no session. `StartedAsync`, once every hosted service started, attaches every registered handler to the fakes, as `WhatsmeowSidecar.Create`'s hook does for a sidecar's new client, so a host whose start failed never had events flowing into handlers whose container it disposes, as ADR 0034 does for the connect; the window ADR 0034 leaves for a later service's `StartedAsync` stays.
  `StoppedAsync` takes them off again, because the fake is the test's and outlives the host's container, then cancels the handlers still running and waits for them, with the host's shutdown timeout, as the sidecar's stop does; a handler that throws once the host stopped it is not a handler error, since on a real session its event would come again.
  Every declared session needs a fake, or `StartingAsync` throws `InvalidOperationException` naming it: a real client of a sidecar that never started would fail every call.
  A session created at runtime through `WhatsmeowSidecar.Sessions` cannot be used in such a host.
  An application that replaces a keyed registration by hand, without `UseFakeClient`, keeps ADR 0034's behaviour.
- **Packaging.** `Whatsmeow.Testing` uses internals of `Whatsmeow.Client` and `Whatsmeow.Extensions.Hosting`, so it depends on both at exactly its own version (`PackExactVersion`, [ADR 0023](0023-native-packages-build-pin-and-find.md)), which `build/ci.sh install` checks; it is MPL-2.0, tracks its public API in `PublicAPI.*.txt`, and is rooted in `tests/AotSmoke`.
  A consumer of the fake gets the hosting package's dependencies too; a separate hosting testing package waits for a consumer who needs the fake without them.
- **Tests.** `Whatsmeow.Testing.Tests` uses only the fake's public API, and no project gives it internals, so the compiler holds the rule (docs/guidelines/tests.md).

## Consequences

- A new `IWhatsAppClient` member fails the build of `Whatsmeow.Testing` until the fake implements it (CS0535), in the same change; a default interface member would get past that, so `IWhatsAppClient` gets none.
- A new event payload fails `EventLaneTests` until it has a sample, and its record a case in `EventMapping.Lane`; nothing makes the compiler check that switch, since C# has no closed hierarchies yet [inferred: C# 14].
- A handler that raises an event of its own lane gets `InvalidOperationException`, awaited or not, while `WaitForAsync` from a handler for an event of its own chat still waits forever, as on a real session.
- That the host's stop waits for a fake's running handler is not killed by a test: telling a stop that waits from one that has not finished yet needs a clock, and no test sleeps (PR #79's mutation table).
- A fake's handler failures are not logged: the fake has no logger, and `OnHandlerError` is how a test sees them.
- Tests: `Whatsmeow.Testing.Tests` (`EventTests`, `CallTests`, `HostingTests`, `ExceptionTests`) and `Whatsmeow.Client.Tests/Events/EventLaneTests.cs`; every existing test, the integration tests of the events included, runs unchanged on the extracted pipeline.
