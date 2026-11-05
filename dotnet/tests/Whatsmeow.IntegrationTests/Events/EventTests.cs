using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>The Events slice through the public client, on the shared fake-mode sidecar (ARCHITECTURE §7.3, §8.4).</summary>
public sealed class EventTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_ReceiveAMessage_TheReceiptGoesOutAndTheHandlerGetsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-fullcycle", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		_ = await fixture.Fake.DeliverMessageAsync(
			new DeliverMessageRequest
			{
				SessionId = "events-fullcycle",
				Id = "M1",
				Chat = "120363000000000000@g.us",
				Sender = "111:2@lid",
				SenderAlt = "5511999999999@s.whatsapp.net",
				RecipientAlt = "222@lid",
				IsFromMe = true,
				PushName = "Ana",
				Text = "hello",
				MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
			},
			cancellationToken: ct
		);

		// whatsmeow answers a decrypted message with a receipt only once the handler recorded it (invariant 2); one the
		// account's other device sent, with type sender (receipt.go:282-286).
		var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "events-fullcycle" }, cancellationToken: ct)).Sent;
		Assert.Equal(new Receipt { Id = "M1", Type = "sender" }, sent.Receipt);

		Assert.Equal(
			new MessageReceived
			{
				SessionId = SessionId.From("events-fullcycle"),
				Id = MessageId.From("M1"),
				Source = new MessageSource
				{
					Chat = new Jid { User = "120363000000000000", Server = "g.us" },
					Sender = new Jid { User = "111", Device = 2, Server = "lid" },
					SenderAlt = new Jid { User = "5511999999999", Server = "s.whatsapp.net" },
					RecipientAlt = new Jid { User = "222", Server = "lid" },
					IsFromMe = true,
				},
				PushName = "Ana",
				Timestamp = Deliveries.At,
				Content = new TextContent { Text = "hello" },
			},
			await received.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GroupMessage_WithANewSenderKey_TheApplicationGetsTheContentOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Group = "120363000000000000@g.us";
		var client = await SessionSetup.LinkedAsync(fixture, "events-senderkey", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		// The stanza's sender-key part and its content part are dispatched with the same info, the sender key first.
		var withSenderKey = Deliveries.Message("events-senderkey", "M1", Group);
		withSenderKey.SenderKey = true;
		_ = await fixture.Fake.DeliverMessageAsync(withSenderKey, cancellationToken: ct);
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message("events-senderkey", "M2", Group), cancellationToken: ct);

		Assert.Equal(
			[Deliveries.Received("events-senderkey", "M1", Group), Deliveries.Received("events-senderkey", "M2", Group)],
			await received.NextAsync(2, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_DeletesItsOwnSession_TheDeleteReturnsAndTheClientIsDone()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-self-delete", ct);
		var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<MessageReceived>(
			async (_, token) =>
			{
				// As an application does on LoggedOut: the delete stops this session's events, this handler's included.
				await fixture.Sidecar.Sessions.DeleteAsync(SessionId.From("events-self-delete"), token).ConfigureAwait(false);
				deleted.SetResult();
			}
		);
		await client.ConnectAsync(ct);
		var pending = client.WaitForAsync<MessageReceived>(e => e.Id == MessageId.From("never"), TimeSpan.FromMinutes(1), ct);

		await Deliveries.DeliverAsync(fixture.Fake, "events-self-delete", ct, "M1");
		await deleted.Task.WaitAsync(ct);

		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
		_ = Assert.Throws<ObjectDisposedException>(() => client.On<MessageReceived>((_, _) => ValueTask.CompletedTask));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => client.WaitForAsync<MessageReceived>(_ => true, TimeSpan.FromMinutes(1), ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task AMessageWithATimeDateTimeOffsetCannotHold_ArrivesWithTheTimeClamped()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-far-time", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		// Year 11476: a Timestamp the sidecar sends as it gets it, and that .NET's DateTimeOffset cannot hold.
		var far = Deliveries.Message("events-far-time", "M1");
		far.MessageTime = new Timestamp { Seconds = 300_000_000_000 };
		_ = await fixture.Fake.DeliverMessageAsync(far, cancellationToken: ct);

		Assert.Equal(Deliveries.Received("events-far-time", "M1") with { Timestamp = DateTimeOffset.MaxValue }, await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task AMessageWithoutAChat_IsSkippedAndTheStreamGoesOn()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-no-chat", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		// Unusable: a message must say where it is. Skipped and acked, never the end of the stream. No slice records one
		// (#122), so the fake records it as it is (RecordEvent).
		const string Nowhere = """{"message": {"info": {"id": "M1", "source": {"sender": "5511999999999@s.whatsapp.net"}, "messageTime": "2023-11-14T22:13:20Z"}}}""";
		_ = await fixture.Fake.RecordEventAsync(new RecordEventRequest { SessionId = "events-no-chat", EventJson = Nowhere }, cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, "events-no-chat", ct, "M2");

		Assert.Equal(Deliveries.Received("events-no-chat", "M2"), await received.NextAsync(ct));

		// The sidecar's fault, so its log names the field, as an internal error, never the caller's invalid argument.
		var unreadable = Assert.Single(fixture.Logs.Named("EventUnreadable"), r => r.Message.Contains("events-no-chat", StringComparison.Ordinal));
		var error = Assert.IsType<WhatsmeowException>(unreadable.Exception);
		Assert.Equal(WhatsmeowErrorCode.Internal, error.ErrorCode);
		Assert.Contains("MessageSource.chat", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// #122: whatsmeow's ParseWebMessage, for a placeholder resend, accepts a message key without a chat or an id
	/// (client.go:1027-1039; message.go:874-880). The client cannot read such a message, so the sidecar does not record
	/// it, and still answers it.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task AMessageDeliveredWithoutAChatOrAnId_IsNotRecordedAndIsStillAnswered()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "events-not-recorded";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		var nowhere = Deliveries.Message(Session, "M1");
		nowhere.Chat = "";
		var noId = Deliveries.Message(Session, "");
		_ = await fixture.Fake.DeliverMessageAsync(nowhere, cancellationToken: ct);
		_ = await fixture.Fake.DeliverMessageAsync(noId, cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M2");

		Assert.Equal(Deliveries.Received(Session, "M2"), await received.NextAsync(ct));
		var answered = new List<string>();
		ulong after = 0;
		for (var i = 0; i < 3; i++)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session, After = after }, cancellationToken: ct)).Sent;
			after = sent.Index;
			answered.Add(sent.Receipt.Id);
		}

		Assert.Equal(["M1", "", "M2"], answered);
		Assert.DoesNotContain(fixture.Logs.Named("EventUnreadable"), r => r.Message.Contains(Session, StringComparison.Ordinal));
	}

	[Fact(Timeout = Timeout)]
	public async Task ConnectionState_EveryTransitionArrivesInOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-states", ct);
		using var received = new Received<ConnectionStateChanged>(client);

		await client.ConnectAsync(ct);
		var linkedAndConnected = await received.NextAsync(3, ct);
		await client.DisconnectAsync(ct);

		SessionState[] states = [new SessionState.Disconnected(), new SessionState.Connecting(), new SessionState.Connected(), new SessionState.Disconnected()];
		Assert.Equal(
			states.Select(state => new ConnectionStateChanged { SessionId = SessionId.From("events-states"), State = state }),
			[.. linkedAndConnected, await received.NextAsync(ct)]
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_Throws_TheStreamGoesOnAndOnHandlerErrorIsRaised()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-throws", ct);
		var failure = new InvalidOperationException("the handler failed");
		var errors = new TaskCompletionSource<HandlerError>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var onError = client.OnHandlerError(error => errors.TrySetResult(error));
		using var failing = client.On<MessageReceived>((e, _) => e.Id == MessageId.From("M1") ? throw failure : ValueTask.CompletedTask);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		await Deliveries.DeliverAsync(fixture.Fake, "events-throws", ct, "M1", "M2");

		Assert.Equal(new HandlerError { Event = Deliveries.Received("events-throws", "M1"), Exception = failure }, await errors.Task.WaitAsync(ct));
		Assert.Equal([Deliveries.Received("events-throws", "M1"), Deliveries.Received("events-throws", "M2")], await received.NextAsync(2, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task WaitForAsync_IsDispatchedInOrderWithTheHandlers()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-waitfor", ct);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var received = new Received<MessageReceived>(
			client,
			async (e, token) =>
			{
				if (e.Id != MessageId.From("M1"))
					return;
				started.SetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		await client.ConnectAsync(ct);
		var wait = client.WaitForAsync<MessageReceived>(e => e.Id == MessageId.From("M2"), TimeSpan.FromMinutes(1), ct);

		await Deliveries.DeliverAsync(fixture.Fake, "events-waitfor", ct, "M1", "M2");
		await started.Task.WaitAsync(ct);

		// M3 is after M2 in another chat: once it is handled, M2 was read too, and waits behind M1 in its chat.
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message("events-waitfor", "M3", "222@s.whatsapp.net"), cancellationToken: ct);
		Assert.Equal(Deliveries.Received("events-waitfor", "M3", "222@s.whatsapp.net"), await received.NextAsync(ct));
		Assert.False(wait.IsCompleted, "WaitForAsync saw M2 before the handler of M1, in the same chat, finished");

		release.SetResult();
		Assert.Equal(Deliveries.Received("events-waitfor", "M2"), await wait);
	}

	[Fact(Timeout = Timeout)]
	public async Task Undecryptable_TheApplicationSeesItThenTheRecoveredMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-undecryptable", ct);
		using var received = new Received<WhatsAppEvent>(client);
		await client.ConnectAsync(ct);

		_ = await fixture.Fake.DeliverUndecryptableMessageAsync(
			new DeliverUndecryptableMessageRequest
			{
				SessionId = "events-undecryptable",
				Id = "M1",
				Chat = Deliveries.Chat,
				Sender = Deliveries.Chat,
				HidePlaceholder = true,
				MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
			},
			cancellationToken: ct
		);
		await Deliveries.DeliverAsync(fixture.Fake, "events-undecryptable", ct, "M1");

		var messages = new List<WhatsAppEvent>();
		while (messages.Count < 2)
		{
			if (await received.NextAsync(ct) is not ConnectionStateChanged and var e)
				messages.Add(e);
		}

		var expected = Deliveries.Received("events-undecryptable", "M1");
		Assert.Equal(
			[
				new UndecryptableMessageReceived
				{
					SessionId = expected.SessionId,
					Id = expected.Id,
					Source = expected.Source,
					PushName = null,
					Timestamp = Deliveries.At,
					HidePlaceholder = true,
				},
				expected,
			],
			messages
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task SlowHandler_EveryMessageIsStillReceiptedAndNoneIsDropped()
	{
		var ct = TestContext.Current.CancellationToken;
		const int Count = 200;
		var client = await SessionSetup.LinkedAsync(fixture, "events-slow", ct);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var received = new Received<MessageReceived>(client, (_, token) => release.Task.WaitAsync(token));
		await client.ConnectAsync(ct);
		var ids = Enumerable.Range(1, Count).Select(i => $"M{i}").ToArray();

		// The handlers are all held, more events than the client buffers: the sidecar's handler never waits for .NET
		// (invariant 1), so WhatsApp gets every receipt, in order.
		await Deliveries.DeliverAsync(fixture.Fake, "events-slow", ct, ids);
		ulong after = 0;
		foreach (var id in ids)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "events-slow", After = after }, cancellationToken: ct)).Sent;
			Assert.Equal(new Receipt { Id = id, Type = "inactive" }, sent.Receipt);
			after = sent.Index;
		}

		release.SetResult();
		Assert.Equal(ids.Select(id => Deliveries.Received("events-slow", id)), await received.NextAsync(Count, ct));
	}
}
