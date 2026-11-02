using System.Collections.Immutable;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageSource = Whatsmeow.Events.MessageSource;
using Raise = Whatsmeow.IntegrationTests.Fake.RaisePermanentDisconnectRequest;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// The Messaging slice through the public client, on the shared fake-mode sidecar (ARCHITECTURE §7.5, invariant 10).
/// What WhatsApp got is read from the fake's log by condition; "nothing was sent" shows by order: the next send is the
/// first thing logged.
/// A <see cref="ReceiptReceived"/> is compared whole with its <see cref="ReceiptReceived.MessageIds"/> taken from the
/// expected record, then the ids on their own: an <see cref="ImmutableArray{T}"/> field compares by reference in record
/// equality, so two equal receipts built apart are never equal as records.
/// </summary>
public sealed class MessagingTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private Task<IWhatsAppClient> ConnectedAsync(string session, CancellationToken ct) => SessionSetup.ConnectedAsync(fixture, session, ct);

	private async Task<Sent> SentAsync(string session, CancellationToken ct, ulong after = 0) =>
		(await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;

	private async Task RegisterAsync(string phoneNumberJid, string lid, CancellationToken ct) =>
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = phoneNumberJid, Lid = lid }, cancellationToken: ct).ConfigureAwait(false);

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SendTextToAPhoneNumber_GoesToItsLid_AndItsReceiptsArrive()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-fullcycle";
		await RegisterAsync("5511900000101@s.whatsapp.net", "900000101@lid", ct);
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var receipts = new Received<ReceiptReceived>(client);
		await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		var id = MessageId.New();

		var result = await client.SendTextAsync(Jid.Parse("5511900000101@s.whatsapp.net"), "hello", id, ct);

		// WhatsApp got the text, sent to the LID, with the client's id; the result says where it went and when.
		var sent = await SentAsync(Session, ct);
		Assert.Equal(
			new SentMessage
			{
				Id = id.Value,
				Chat = "900000101@lid",
				Message = new E2E.Message { Conversation = "hello" },
				MessageTime = sent.Message.MessageTime,
			},
			sent.Message
		);
		Assert.Equal(
			new SendResult
			{
				Id = id,
				Chat = new Jid { User = "900000101", Server = "lid" },
				Timestamp = sent.Message.MessageTime.ToDateTimeOffset(),
			},
			result
		);

		// The recipient's phone receives it, then reads it.
		var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_300);
		foreach (var type in new[] { "", "read" })
		{
			_ = await fixture.Fake.DeliverReceiptAsync(
				new DeliverReceiptRequest
				{
					SessionId = Session,
					Chat = "900000101@lid",
					Sender = "900000101@lid",
					SenderAlt = "5511900000101@s.whatsapp.net",
					MessageIds = { id.Value },
					Type = type,
					ReceiptTime = Timestamp.FromDateTimeOffset(at),
				},
				cancellationToken: ct
			);
		}

		var delivered = new ReceiptReceived
		{
			SessionId = SessionId.From(Session),
			Source = new MessageSource
			{
				Chat = Jid.Parse("900000101@lid"),
				Sender = Jid.Parse("900000101@lid"),
				SenderAlt = Jid.Parse("5511900000101@s.whatsapp.net"),
				RecipientAlt = null,
				IsFromMe = false,
			},
			MessageIds = [id],
			Timestamp = at,
			Type = ReceiptType.Delivered,
		};
		var got = await receipts.NextAsync(2, ct);
		Assert.Equal(delivered, got[0]);
		Assert.Equal(delivered with { Type = ReceiptType.Read }, got[1]);

		// Each was acked to WhatsApp once it was recorded (invariant 2).
		var firstAck = await SentAsync(Session, ct, sent.Index);
		Assert.Equal(new Ack { Class = "receipt", Id = id.Value, Type = "" }, firstAck.Ack);
		Assert.Equal(new Ack { Class = "receipt", Id = id.Value, Type = "read" }, (await SentAsync(Session, ct, firstAck.Index)).Ack);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendRaw_SendsTheMessageAsGiven()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-raw";
		var client = await ConnectedAsync(Session, ct);
		var message = new E2E.Message
		{
			ExtendedTextMessage = new E2E.ExtendedTextMessage { Text = "see https://example.org", MatchedText = "https://example.org" },
		};

#pragma warning disable WMN1001 // Justification: the raw send is what is under test
		var result = await client.SendRawAsync(Jid.Parse("120363000000000101@g.us"), message, cancellationToken: ct);
#pragma warning restore WMN1001

		var sent = await SentAsync(Session, ct);
		Assert.Equal(
			new SentMessage
			{
				Id = result.Id.Value,
				Chat = "120363000000000101@g.us",
				Message = message,
				MessageTime = sent.Message.MessageTime,
			},
			sent.Message
		);
		Assert.Matches("^3EB0[0-9A-F]{18}$", result.Id.Value);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendRaw_AMissingRequiredField_ThrowsValidation_AndNothingIsSent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-required";
		var client = await ConnectedAsync(Session, ct);

		// VideoEndCard's four fields are proto2 required; Google.Protobuf serializes it anyway.
		var partial = new E2E.Message { ExtendedTextMessage = new E2E.ExtendedTextMessage { Text = "hi", EndCardTiles = { new E2E.VideoEndCard() } } };

#pragma warning disable WMN1001 // Justification: the raw send is what is under test
		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.SendRawAsync(Jid.Parse("900000102@lid"), partial, cancellationToken: ct));
#pragma warning restore WMN1001

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		var next = await client.SendTextAsync(Jid.Parse("900000102@lid"), "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await SentAsync(Session, ct)).Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_ToARecipientNotOnWhatsApp_ThrowsRecipientNotFound_AndARepeatOnceTheyJoinedSends()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-not-on-whatsapp";
		var client = await ConnectedAsync(Session, ct);
		var to = Jid.Parse("5511900000103@s.whatsapp.net");
		var id = MessageId.New();

		var error = await Assert.ThrowsAsync<RecipientNotFoundException>(() => client.SendTextAsync(to, "hi", id, ct));
		Assert.Equal(WhatsmeowErrorCode.RecipientNotOnWhatsApp, error.ErrorCode);
		// whatsmeow's error names the number; the message carries none of it, nor any other digit (ARCHITECTURE §7.8).
		Assert.DoesNotMatch("[0-9]", error.Message);

		// Nothing was sent, so nothing was recorded: the same id sends once the number is on WhatsApp.
		await RegisterAsync("5511900000103@s.whatsapp.net", "900000103@lid", ct);
		var result = await client.SendTextAsync(to, "hi", id, ct);

		var sent = await SentAsync(Session, ct);
		Assert.Equal((id.Value, "900000103@lid"), (sent.Message.Id, sent.Message.Chat));
		Assert.Equal(new SendResult { Id = id, Chat = Jid.Parse("900000103@lid"), Timestamp = sent.Message.MessageTime.ToDateTimeOffset() }, result);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_TheSameIdTwice_WhatsAppGetsOneMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-same-id";
		var client = await ConnectedAsync(Session, ct);
		var to = Jid.Parse("900000104@lid");
		var id = MessageId.New();

		var first = await client.SendTextAsync(to, "once", id, ct);
		var again = await client.SendTextAsync(to, "once", id, ct);
		var other = await client.SendTextAsync(to, "other", cancellationToken: ct);

		Assert.Equal(first, again);
		var sent = await SentAsync(Session, ct);
		Assert.Equal(id.Value, sent.Message.Id);
		Assert.Equal(other.Id.Value, (await SentAsync(Session, ct, sent.Index)).Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_CancelledAfterTheHandOff_ARepeatIsOutcomeUnknown_AndNothingIsSentAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-cancel";
		var client = await ConnectedAsync(Session, ct);
		var to = Jid.Parse("900000105@lid");
		var id = MessageId.New();
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

		var held = client.SendTextAsync(to, "maybe", id, cancel.Token);
		var sent = await SentAsync(Session, ct);
		await cancel.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
		var error = await Assert.ThrowsAsync<OutcomeUnknownException>(() => client.SendTextAsync(to, "maybe", id, ct));
		Assert.Equal(WhatsmeowErrorCode.OutcomeUnknown, error.ErrorCode);

		// Never sent again: the next thing WhatsApp gets is the next send (invariant 10).
		var next = await client.SendTextAsync(to, "next", cancellationToken: ct);
		Assert.Equal((id.Value, next.Id.Value), (sent.Message.Id, (await SentAsync(Session, ct, sent.Index)).Message.Id));
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_RateLimitedOnce_IsBackedOffAndSent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-rate-once";
		var client = await ConnectedAsync(Session, ct);
		_ = await fixture.Fake.RateLimitNextSendsAsync(new RateLimitNextSendsRequest { SessionId = Session, Count = 1 }, cancellationToken: ct);

		var result = await client.SendTextAsync(Jid.Parse("900000106@lid"), "hi", cancellationToken: ct);

		Assert.Equal(result.Id.Value, (await SentAsync(Session, ct)).Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_RateLimitedPastTheBackoff_ThrowsRateLimited_AndNothingIsSent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-rate-limited";
		var client = await ConnectedAsync(Session, ct);
		var to = Jid.Parse("900000107@lid");
		_ = await fixture.Fake.RateLimitNextSendsAsync(new RateLimitNextSendsRequest { SessionId = Session, Count = 4 }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<RateLimitedException>(() => client.SendTextAsync(to, "hi", cancellationToken: ct));

		Assert.Equal(WhatsmeowErrorCode.RateLimited, error.ErrorCode);
		var next = await client.SendTextAsync(to, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await SentAsync(Session, ct)).Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_BeforePairing_ThrowsNotLoggedIn()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("messaging-unpaired"), ct);

		var error = await Assert.ThrowsAsync<NotLoggedInException>(() => client.SendTextAsync(Jid.Parse("900000108@lid"), "hi", cancellationToken: ct));

		Assert.Equal(WhatsmeowErrorCode.NotLoggedIn, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_WhileNotConnected_ThrowsNotConnectedAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "messaging-offline", ct);

		var error = await Assert.ThrowsAsync<NotConnectedException>(() => client.SendTextAsync(Jid.Parse("900000109@lid"), "hi", cancellationToken: ct));

		Assert.Equal(WhatsmeowErrorCode.NotConnected, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_WhileTemporarilyBanned_ThrowsTemporarilyBannedUntilItsEnd()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-banned";
		var client = await ConnectedAsync(Session, ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());
		_ = await fixture.Fake.RaisePermanentDisconnectAsync(
			new Raise { SessionId = Session, TemporaryBan = new Raise.Types.TemporaryBan { Code = 101, Expire = Duration.FromTimeSpan(TimeSpan.FromHours(1)) } },
			cancellationToken: ct
		);
		var state = Assert.IsType<SessionState.TempBanned>(await watch.NextAsync());

		var error = await Assert.ThrowsAsync<TemporarilyBannedException>(() => client.SendTextAsync(Jid.Parse("900000110@lid"), "hi", cancellationToken: ct));

		Assert.Equal((WhatsmeowErrorCode.TemporarilyBanned, state.Until), (error.ErrorCode, error.Until));
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_TheSessionIsLoggedOut_TheHeldSendIsOutcomeUnknown_AndTheNextFailsLoggedOut()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-logged-out";
		var client = await ConnectedAsync(Session, ct);
		var to = Jid.Parse("900000111@lid");
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);

		var held = client.SendTextAsync(to, "in flight", cancellationToken: ct);
		_ = await SentAsync(Session, ct);
		var behind = client.SendTextAsync(to, "behind it", cancellationToken: ct);
		_ = await fixture.Fake.RaisePermanentDisconnectAsync(new Raise { SessionId = Session, LoggedOut = new Raise.Types.LoggedOut { Reason = 401 } }, cancellationToken: ct);

		// The one handed off may have reached WhatsApp; the one behind it never left (message.proto).
		_ = await Assert.ThrowsAsync<OutcomeUnknownException>(() => held);
		var error = await Assert.ThrowsAsync<SessionLoggedOutException>(() => behind);
		Assert.Equal(WhatsmeowErrorCode.LoggedOut, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task MarkRead_SendsOneReadReceiptForTheMessages()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-mark-read";
		var client = await ConnectedAsync(Session, ct);

		await client.MarkReadAsync(
			Jid.Parse("120363000000000112@g.us"),
			[MessageId.From("M1"), MessageId.From("M2")],
			Jid.Parse("111:2@lid"),
			ct
		);

		Assert.Equal(
			new ReadReceipt { Chat = "120363000000000112@g.us", Participant = "111@lid", MessageIds = { "M1", "M2" } },
			(await SentAsync(Session, ct)).Read
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task MarkRead_InAGroupWithoutTheSender_ThrowsValidation()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await ConnectedAsync("messaging-mark-read-group", ct);

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.MarkReadAsync(Jid.Parse("120363000000000113@g.us"), [MessageId.From("M1")], cancellationToken: ct));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
	}

	/// <summary>
	/// A sidecar error message reaches the application, and its logs, as the exception's message: it names no phone
	/// number or JID user part, even when the sidecar echoes the JID it refused (ADR 0039).
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task SendText_ToADevice_ThrowsWithoutTheNumber()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await ConnectedAsync("messaging-device-redacted", ct);

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.SendTextAsync(Jid.Parse("5511900000114:3@s.whatsapp.net"), "hi", cancellationToken: ct));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.DoesNotContain("5511900000114", error.Message, StringComparison.Ordinal);
		Assert.Contains(":3@s.whatsapp.net\" names a device", error.Message, StringComparison.Ordinal);
	}
}
