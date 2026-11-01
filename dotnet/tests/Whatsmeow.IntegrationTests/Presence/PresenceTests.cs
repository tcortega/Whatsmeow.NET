using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Presence;
using MessageSource = Whatsmeow.Events.MessageSource;
using Sent = Whatsmeow.IntegrationTests.Fake.Sent;

namespace Whatsmeow.IntegrationTests.Presence;

/// <summary>The Presence slice and the ephemeral events through the public client, on the shared fake-mode sidecar.</summary>
public sealed class PresenceTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SetPresenceSubscribeAndType_WhatsAppIsToldAndTheContactsPresenceArrives()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-fullcycle";
		const string Group = "120363000000000000@g.us";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		using var presences = new Received<PresenceChanged>(client);
		using var chatPresences = new Received<ChatPresenceChanged>(client);

		await client.SendPresenceAsync(PresenceState.Available, ct);
		await client.SubscribePresenceAsync(Jid.Parse(Presences.User), ct);
		await client.SendChatPresenceAsync(Jid.Parse(Group), ChatPresenceState.Typing, ct);
		await client.SendChatPresenceAsync(Jid.Parse(Group), ChatPresenceState.Recording, ct);
		await client.SendChatPresenceAsync(Jid.Parse(Group), ChatPresenceState.Paused, ct);

		Assert.Equal(
			[
				new Sent { SessionId = Session, Presence = new Fake.Presence { Type = "available", Name = "Ana" } },
				new Sent { SessionId = Session, SubscribePresence = new SubscribePresence { To = Presences.User } },
				new Sent { SessionId = Session, ChatPresence = new ChatPresence { To = Group, State = "composing" } },
				new Sent { SessionId = Session, ChatPresence = new ChatPresence { To = Group, State = "composing", Media = "audio" } },
				new Sent { SessionId = Session, ChatPresence = new ChatPresence { To = Group, State = "paused" } },
			],
			await SentAsync(Session, 5, ct)
		);

		await Presences.DeliverAsync(fixture.Fake, ct, Presences.Of(Session, 1));
		_ = await fixture.Fake.DeliverChatPresenceAsync(
			new DeliverChatPresenceRequest { SessionId = Session, Chat = Group, Sender = "111:2@lid", State = "composing", Media = "audio" },
			cancellationToken: ct
		);

		Assert.Equal(Presences.Changed(Session, 1), await presences.NextAsync(ct));
		Assert.Equal(
			new ChatPresenceChanged
			{
				SessionId = SessionId.From(Session),
				Source = new MessageSource
				{
					Chat = Jid.Parse(Group),
					Sender = new Jid { User = "111", Device = 2, Server = "lid" },
					SenderAlt = null,
					RecipientAlt = null,
					IsFromMe = false,
				},
				State = ChatPresenceState.Recording,
			},
			await chatPresences.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task ChatPresence_TypingAndPaused_ArriveInOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-typing";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		using var chatPresences = new Received<ChatPresenceChanged>(client);

		foreach (var state in new[] { "composing", "paused" })
		{
			_ = await fixture.Fake.DeliverChatPresenceAsync(
				new DeliverChatPresenceRequest { SessionId = Session, Chat = Deliveries.Chat, Sender = Deliveries.Chat, State = state },
				cancellationToken: ct
			);
		}

		var source = new MessageSource { Chat = Jid.Parse(Deliveries.Chat), Sender = Jid.Parse(Deliveries.Chat), SenderAlt = null, RecipientAlt = null, IsFromMe = false };
		Assert.Equal(
			[
				new ChatPresenceChanged { SessionId = SessionId.From(Session), Source = source, State = ChatPresenceState.Typing },
				new ChatPresenceChanged { SessionId = SessionId.From(Session), Source = source, State = ChatPresenceState.Paused },
			],
			await chatPresences.NextAsync(2, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Presence_LastSeenHidden_ArrivesOfflineWithNoLastSeen()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-hidden";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		using var presences = new Received<PresenceChanged>(client);

		await Presences.DeliverAsync(fixture.Fake, ct, new DeliverPresenceRequest { SessionId = Session, From = Presences.User, Unavailable = true });

		Assert.Equal(
			new PresenceChanged { SessionId = SessionId.From(Session), From = Jid.Parse(Presences.User), State = PresenceState.Unavailable, LastSeen = null },
			await presences.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendPresence_BeforeThePushNameArrived_ThrowsNoPushNameAndSucceedsAfter()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-no-push-name";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);

		var refused = await Assert.ThrowsAsync<WhatsmeowException>(() => client.SendPresenceAsync(PresenceState.Available, ct));
		Assert.Equal(WhatsmeowErrorCode.NoPushName, refused.ErrorCode);

		_ = await fixture.Fake.SetPushNameAsync(new SetPushNameRequest { SessionId = Session, Name = "Ana" }, cancellationToken: ct);
		await client.SendPresenceAsync(PresenceState.Unavailable, ct);

		Assert.Equal(
			new Sent { SessionId = Session, Presence = new Fake.Presence { Type = "unavailable", Name = "Ana" } },
			Assert.Single(await SentAsync(Session, 1, ct))
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Presence_ASessionThatIsNotOnline_ThrowsAtOnceAndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var linked = await SessionSetup.LinkedAsync(fixture, "presence-offline", ct);
		var created = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("presence-unpaired"), ct);
		var user = Jid.Parse(Presences.User);

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => linked.SendPresenceAsync(PresenceState.Available, ct));
		_ = await Assert.ThrowsAsync<NotConnectedException>(() => linked.SubscribePresenceAsync(user, ct));
		_ = await Assert.ThrowsAsync<NotConnectedException>(() => linked.SendChatPresenceAsync(user, ChatPresenceState.Typing, ct));
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => created.SendPresenceAsync(PresenceState.Available, ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => linked.SendPresenceAsync((PresenceState)42, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Overflow_NoStreamOpen_TheOldestAreDroppedAndTheCountArrives()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-overflow";

		// Not connected in this process, so no stream is open: the sidecar keeps the newest, and counts the rest.
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var presences = new Received<PresenceChanged>(client);
		using var dropped = new Received<EphemeralEventsDropped>(client);
		await Presences.DeliverAsync(fixture.Fake, ct, [.. Enumerable.Range(0, Presences.Kept + 3).Select(n => Presences.Of(Session, n))]);

		await client.ConnectAsync(ct);

		Assert.Equal(new EphemeralEventsDropped { SessionId = SessionId.From(Session), Count = 3 }, await dropped.NextAsync(ct));
		Assert.Equal(
			Enumerable.Range(3, Presences.Kept).Select(n => Presences.Changed(Session, n)),
			await presences.NextAsync(Presences.Kept, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_ThrowsOnPresence_OnHandlerErrorIsRaisedAndTheNextOneArrives()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-throws";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		var errors = new List<HandlerError>();
		using var onError = client.OnHandlerError(errors.Add);
		var boom = new InvalidOperationException("handler bug");
		using var throwing = client.On<PresenceChanged>((e, _) => e.LastSeen == Presences.LastSeen(1) ? throw boom : ValueTask.CompletedTask);
		using var presences = new Received<PresenceChanged>(client);

		await Presences.DeliverAsync(fixture.Fake, ct, Presences.Of(Session, 1), Presences.Of(Session, 2));

		Assert.Equal([Presences.Changed(Session, 1), Presences.Changed(Session, 2)], await presences.NextAsync(2, ct));
		Assert.Equal([new HandlerError { Event = Presences.Changed(Session, 1), Exception = boom }], errors);
	}

	[Fact(Timeout = Timeout)]
	public async Task ChatPresence_AStateThisClientDoesNotKnow_IsSkippedAndTheStreamGoesOn()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-unknown-state";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		using var chatPresences = new Received<ChatPresenceChanged>(client);

		foreach (var state in new[] { "recording-video", "composing" })
		{
			_ = await fixture.Fake.DeliverChatPresenceAsync(
				new DeliverChatPresenceRequest { SessionId = Session, Chat = Deliveries.Chat, Sender = Deliveries.Chat, State = state },
				cancellationToken: ct
			);
		}

		Assert.Equal(ChatPresenceState.Typing, (await chatPresences.NextAsync(ct)).State);
	}

	[Fact(Timeout = Timeout)]
	public async Task Burst_PresenceHandlersAreStuck_MessagesStillArrive()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-burst";
		var client = await Presences.OnlineAsync(fixture, Session, ct);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		// Every presence handler holds its lane until the end of the test: far more than the dispatcher runs at once.
		using var stuck = client.On<PresenceChanged>(async (_, token) => await release.Task.WaitAsync(token).ConfigureAwait(false));
		using var messages = new Received<MessageReceived>(client);

		await Presences.DeliverAsync(fixture.Fake, ct, [.. Enumerable.Range(0, 2_000).Select(n => Presences.Of(Session, n, $"55119{n:D8}@s.whatsapp.net"))]);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1", "M2", "M3", "M4", "M5");

		Assert.Equal(
			Enumerable.Range(1, 5).Select(i => Deliveries.Received(Session, $"M{i}")),
			await messages.NextAsync(5, ct)
		);
		release.SetResult();
	}

	/// <summary>The next <paramref name="count"/> things the session's client sent, without their log index.</summary>
	private async Task<IReadOnlyList<Sent>> SentAsync(string session, int count, CancellationToken cancellationToken)
	{
		var sent = new List<Sent>();
		ulong after = 0;
		for (var i = 0; i < count; i++)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: cancellationToken).ConfigureAwait(false)).Sent;
			after = next.Index;
			sent.Add(next.Clone());
			sent[^1].Index = 0;
		}

		return sent;
	}
}
