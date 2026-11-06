using Google.Protobuf.WellKnownTypes;
using Whatsmeow.ChatState;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using StarredMessage = Whatsmeow.IntegrationTests.Fake.GetPhoneChatStateResponse.Types.StarredMessage;

namespace Whatsmeow.IntegrationTests.ChatState;

/// <summary>
/// The Chat state slice through the public client, on the shared fake-mode sidecar. The fake WhatsApp keeps each
/// session's app state, which is what its phone shows; each test uses a session of its own.
/// </summary>
public sealed class ChatStateTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private static readonly Jid s_chat = new() { User = "5511930000001", Server = "s.whatsapp.net" };
	private static readonly Jid s_group = new() { User = "120363000000000031", Server = "g.us" };
	private static readonly Jid s_member = new() { User = "5511930000002", Server = "s.whatsapp.net" };

	/// <summary>A mute end with milliseconds, which the app state keeps.</summary>
	private static readonly DateTimeOffset s_until = new(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

	private async Task<GetPhoneChatStateResponse> PhoneAsync(string session, Jid chat, CancellationToken cancellationToken) =>
		await fixture.Fake.GetPhoneChatStateAsync(new GetPhoneChatStateRequest { SessionId = session, Chat = chat.ToString() }, cancellationToken: cancellationToken).ConfigureAwait(false);

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_EachChatWrite_ThePhoneShowsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-write";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		using var changes = new Received<ChatStateChanged>(client);

		await client.MuteChatAsync(s_chat, s_until, ct);
		Assert.Equal(new GetPhoneChatStateResponse { Muted = true, MuteUntil = Timestamp.FromDateTimeOffset(s_until) }, await PhoneAsync(session, s_chat, ct));

		// The write comes back as a change, as whatsmeow's sync after it brings it (appstate.go:636-644).
		ChatStateChecks.ValidateMuteEcho(await changes.NextAsync(ct), s_chat, s_until);

		await client.MuteChatAsync(s_chat, cancellationToken: ct);
		Assert.Equal(new GetPhoneChatStateResponse { Muted = true }, await PhoneAsync(session, s_chat, ct));

		await client.UnmuteChatAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));

		// A state the chat already has is set again, and succeeds.
		await client.PinChatAsync(s_chat, ct);
		await client.PinChatAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse { Pinned = true }, await PhoneAsync(session, s_chat, ct));

		await client.UnpinChatAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));

		// Archiving unpins too.
		await client.PinChatAsync(s_chat, ct);
		await client.ArchiveChatAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse { Archived = true }, await PhoneAsync(session, s_chat, ct));

		await client.UnarchiveChatAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));

		await client.MarkChatUnreadAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse { Unread = true }, await PhoneAsync(session, s_chat, ct));

		await client.MarkChatReadAsync(s_chat, ct);
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task StarMessage_DirectAndGroup_ThePhoneShowsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-star";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);

		// In a direct chat, and for the account's own messages, the app state names no sender: "0". A message's source is
		// what a MessageReceived carries.
		var fromThem = new MessageSource { Chat = s_chat, Sender = s_chat, SenderAlt = null, RecipientAlt = null, IsFromMe = false };
		await client.StarMessageAsync(MessageId.From("M1"), fromThem, ct);
		await client.StarMessageAsync(MessageId.From("M2"), fromThem with { Sender = s_member, IsFromMe = true }, ct);
		Assert.Equal(
			new GetPhoneChatStateResponse
			{
				Starred =
				{
					new StarredMessage { MessageId = "M1", IsFromMe = false, Sender = "0" },
					new StarredMessage { MessageId = "M2", IsFromMe = true, Sender = "0" },
				},
			},
			await PhoneAsync(session, s_chat, ct)
		);

		await client.UnstarMessageAsync(MessageId.From("M1"), fromThem, ct);
		Assert.Equal(
			new GetPhoneChatStateResponse { Starred = { new StarredMessage { MessageId = "M2", IsFromMe = true, Sender = "0" } } },
			await PhoneAsync(session, s_chat, ct)
		);

		// In a group, a message from someone else names its sender, without the device, and needs a user there.
		var inGroup = fromThem with { Chat = s_group, Sender = s_member with { Device = 2 } };
		await client.StarMessageAsync(MessageId.From("G1"), inGroup, ct);
		Assert.Equal(
			new GetPhoneChatStateResponse { Starred = { new StarredMessage { MessageId = "G1", IsFromMe = false, Sender = s_member.ToString() } } },
			await PhoneAsync(session, s_group, ct)
		);

		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.StarMessageAsync(MessageId.From("G2"), inGroup with { Sender = s_group }, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task MuteChat_UntilAtOrBeforeTheEpoch_IsInvalidAndThePhoneIsUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-mute-epoch";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);

		// whatsmeow reads an end of 0 or less as forever, so such an end would mute the chat for good.
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.MuteChatAsync(s_chat, default(DateTimeOffset), ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.MuteChatAsync(s_chat, DateTimeOffset.UnixEpoch, ct));
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task PhoneChange_AppStateSyncs_HandlerGetsChatStateChangedAndKnowsAFullSync()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-phone";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		using var changes = new Received<ChatStateChanged>(client);
		var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, 123, TimeSpan.Zero);

		_ = await fixture.Fake.SyncChatStateAsync(
			new SyncChatStateRequest
			{
				SessionId = session,
				Chat = s_chat.ToString(),
				ChangeTime = Timestamp.FromDateTimeOffset(at),
				Change = new PhoneChatChange { Mute = new PhoneChatChange.Types.Mute { Muted = true, Until = Timestamp.FromDateTimeOffset(s_until) } },
			},
			cancellationToken: ct
		);

		var expected = new ChatStateChanged
		{
			SessionId = SessionId.From(session),
			Chat = s_chat,
			Timestamp = at,
			FromFullSync = false,
			Change = new ChatStateChange.Mute { IsMuted = true, Until = s_until },
		};
		Assert.Equal(expected, await changes.NextAsync(ct));

		// A full sync, as after pairing, marks each event.
		_ = await fixture.Fake.SyncChatStateAsync(
			new SyncChatStateRequest
			{
				SessionId = session,
				Chat = s_group.ToString(),
				ChangeTime = Timestamp.FromDateTimeOffset(at),
				FullSync = true,
				Change = new PhoneChatChange { Star = new PhoneChatChange.Types.Star { MessageId = "G1", Sender = s_member.ToString(), Starred = true } },
			},
			cancellationToken: ct
		);

		Assert.Equal(
			expected with
			{
				Chat = s_group,
				FromFullSync = true,
				Change = new ChatStateChange.Star
				{
					MessageId = MessageId.From("G1"),
					Sender = s_member,
					IsFromMe = false,
					IsStarred = true,
				},
			},
			await changes.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Write_CorruptedCollection_RecoversOnceAndTheWriteSucceeds()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-corrupt";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		_ = await fixture.Fake.CorruptAppStateAsync(new CorruptAppStateRequest { SessionId = session, Collection = "regular_low" }, cancellationToken: ct);

		await client.PinChatAsync(s_chat, ct);

		Assert.Equal(new GetPhoneChatStateResponse { Pinned = true }, await PhoneAsync(session, s_chat, ct));

		// One full sync, then nothing until a message's receipt: shown by order.
		_ = await fixture.Fake.DeliverMessageAsync(new DeliverMessageRequest { SessionId = session, Id = "M1", Chat = s_chat.ToString(), Sender = s_chat.ToString(), Text = "hi" }, cancellationToken: ct);
		Assert.Equal(
			[
				new Sent { SessionId = session, AppStateFullSync = new AppStateFullSync { Collection = "regular_low" } },
				new Sent { SessionId = session, Receipt = new Receipt { Id = "M1", Type = "inactive" } },
			],
			await SentAsync(session, 2, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Write_CorruptionAFullSyncCannotFix_ThrowsWhatsAppErrorAndThePhoneIsUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-unrecoverable";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		_ = await fixture.Fake.CorruptAppStateAsync(new CorruptAppStateRequest { SessionId = session, Collection = "regular_low", Unrecoverable = true }, cancellationToken: ct);

		var e = await Assert.ThrowsAsync<WhatsAppErrorException>(() => client.ArchiveChatAsync(s_chat, ct));

		Assert.Equal(WhatsmeowErrorCode.WhatsAppError, e.ErrorCode);
		Assert.Equal(409, e.Code);
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task PhoneChange_ToACorruptedCollection_IsRecoveredOnceAndArrivesFromTheFullSync()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-phone-corrupt";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		using var changes = new Received<ChatStateChanged>(client);
		var at = new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero);
		_ = await fixture.Fake.CorruptAppStateAsync(new CorruptAppStateRequest { SessionId = session, Collection = "regular_high" }, cancellationToken: ct);

		// The sync of the phone's change fails; the session's worker recovers the collection, whose full sync brings it.
		_ = await fixture.Fake.SyncChatStateAsync(
			new SyncChatStateRequest
			{
				SessionId = session,
				Chat = s_chat.ToString(),
				ChangeTime = Timestamp.FromDateTimeOffset(at),
				Change = new PhoneChatChange { Mute = new PhoneChatChange.Types.Mute { Muted = true } },
			},
			cancellationToken: ct
		);

		Assert.Equal(
			new ChatStateChanged
			{
				SessionId = SessionId.From(session),
				Chat = s_chat,
				Timestamp = at,
				FromFullSync = true,
				Change = new ChatStateChange.Mute { IsMuted = true, Until = null },
			},
			await changes.NextAsync(ct)
		);

		// One full sync, then nothing until a message's receipt.
		_ = await fixture.Fake.DeliverMessageAsync(new DeliverMessageRequest { SessionId = session, Id = "M1", Chat = s_chat.ToString(), Sender = s_chat.ToString(), Text = "hi" }, cancellationToken: ct);
		Assert.Equal(
			[
				new Sent { SessionId = session, AppStateFullSync = new AppStateFullSync { Collection = "regular_high" } },
				new Sent { SessionId = session, Receipt = new Receipt { Id = "M1", Type = "inactive" } },
			],
			await SentAsync(session, 2, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Recovery_WhileItRuns_LiveMessagesStillReachTheirHandlers()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-hold";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		using var messages = new Received<MessageReceived>(client);
		_ = await fixture.Fake.CorruptAppStateAsync(new CorruptAppStateRequest { SessionId = session, Collection = "regular_low" }, cancellationToken: ct);
		_ = await fixture.Fake.HoldFullSyncAsync(new HoldFullSyncRequest { SessionId = session, Hold = true }, cancellationToken: ct);

		var pin = client.PinChatAsync(s_chat, ct);
		Assert.Equal(
			new Sent { SessionId = session, AppStateFullSync = new AppStateFullSync { Collection = "regular_low" } },
			(await SentAsync(session, 1, ct))[0]
		);

		// The recovery holds whatsmeow's app state lock, never the node queue or the outbox.
		_ = await fixture.Fake.DeliverMessageAsync(new DeliverMessageRequest { SessionId = session, Id = "M1", Chat = s_chat.ToString(), Sender = s_chat.ToString(), Text = "hi" }, cancellationToken: ct);
		Assert.Equal(MessageId.From("M1"), (await messages.NextAsync(ct)).Id);
		Assert.False(pin.IsCompleted);

		_ = await fixture.Fake.HoldFullSyncAsync(new HoldFullSyncRequest { SessionId = session, Hold = false }, cancellationToken: ct);
		await pin;
		Assert.Equal(new GetPhoneChatStateResponse { Pinned = true }, await PhoneAsync(session, s_chat, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task ChatWrite_NotConnected_ThrowsNotConnectedAtOnceAndThePhoneIsUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-offline";
		var client = await SessionSetup.LinkedAsync(fixture, session, ct);

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => client.MuteChatAsync(s_chat, s_until, ct));
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task ChatWrite_BeforeThePhoneSharedTheAppStateKey_ThrowsNotConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = "chats-no-key";
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(session), ct);
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session), WithoutAppStateKey = true }, cancellationToken: ct);
		await client.ConnectAsync(ct);
		var watch = StateWatch.Start(client, ct);
		await using (watch)
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => client.PinChatAsync(s_chat, ct));
		Assert.Equal(new GetPhoneChatStateResponse(), await PhoneAsync(session, s_chat, ct));
	}

	/// <summary>
	/// #122: whatsmeow ignores the error when it parses a mutation's chat, so an index without one gives the empty JID,
	/// and takes a star's message id unchecked (appstate.go:266-270, 340). The client cannot read either, so the
	/// sidecar does not record them, and the next change still arrives.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task APhoneChangeOfNoChatOrAStarOfNoMessage_IsNeverUnreadable()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "chats-unreadable";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var changes = new Received<ChatStateChanged>(client);
		var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, 123, TimeSpan.Zero);

		foreach (var (chat, change) in new[]
		{
			("", new PhoneChatChange { Pinned = true }),
			(s_chat.ToString(), new PhoneChatChange { Star = new PhoneChatChange.Types.Star { MessageId = "", Starred = true } }),
			(s_chat.ToString(), new PhoneChatChange { Archived = true }),
		})
		{
			_ = await fixture.Fake.SyncChatStateAsync(
				new SyncChatStateRequest { SessionId = Session, Chat = chat, ChangeTime = Timestamp.FromDateTimeOffset(at), Change = change },
				cancellationToken: ct
			);
		}

		Assert.Equal(
			new ChatStateChanged
			{
				SessionId = SessionId.From(Session),
				Chat = s_chat,
				Timestamp = at,
				FromFullSync = false,
				Change = new ChatStateChange.Archive { IsArchived = true },
			},
			await changes.NextAsync(ct)
		);
		Assert.DoesNotContain(fixture.Logs.Named("EventUnreadable"), r => r.Message.Contains(Session, StringComparison.Ordinal));
	}

	/// <summary>The session's first <paramref name="count"/> entries of what it sent, without their log indexes.</summary>
	private async Task<List<Sent>> SentAsync(string session, int count, CancellationToken cancellationToken)
	{
		var sent = new List<Sent>(count);
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
