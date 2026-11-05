using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageSource = Whatsmeow.Events.MessageSource;
using ProtocolType = Whatsmeow.Proto.WAWebProtobufsE2E.ProtocolMessage.Types.Type;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// A direct chat's disappearing messages timer (#106), set and received, through the public client, on the shared
/// fake-mode sidecar: what WhatsApp got is read from the fake's log; a group's timer is in
/// <see cref="Groups.GroupModeLinkTimerTests"/>.
/// </summary>
public sealed class DisappearingTimerTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private static readonly Jid s_peer = Jid.Parse("900000401@lid");

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SetDisappearingTimer_ADirectChat_WhatsAppGetsTheSettingThenItsEnd_AndOneNotOfferedIsRefusedUnsent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "disappearing-timer-direct";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);

		await client.SetDisappearingTimerAsync(s_peer, TimeSpan.FromDays(7), cancellationToken: ct);
		var set = await NextMessageAsync(Session, 0, ct);

		// Only 24 hours, 7 days, 90 days and off are offered; nothing is sent, so the next message is the one after.
		var thrown = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.SetDisappearingTimerAsync(s_peer, TimeSpan.FromHours(12), cancellationToken: ct));
		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, thrown.ErrorCode);

		await client.SetDisappearingTimerAsync(s_peer, TimeSpan.Zero, cancellationToken: ct);
		var off = await NextMessageAsync(Session, set.Index, ct);

		// The protocol message whatsmeow sends for a direct chat (send.go:668-678), timestamped by the sidecar's clock in
		// the send's turn: before WhatsApp's answer, and, for the second, after the first's. The sidecar's tests pin the
		// time with a fake clock; this one bounds the first by a minute.
		foreach (var (sent, seconds, notBefore) in new[] { (set, 604_800u, set.Message.MessageTime.Seconds - 60), (off, 0u, set.Message.MessageTime.Seconds) })
		{
			var at = sent.Message.Message.ProtocolMessage.EphemeralSettingTimestamp;
			Assert.InRange(at, notBefore, sent.Message.MessageTime.Seconds);
			Assert.Equal(
				new E2E.Message
				{
					ProtocolMessage = new E2E.ProtocolMessage { Type = ProtocolType.EphemeralSetting, EphemeralExpiration = seconds, EphemeralSettingTimestamp = at },
				},
				sent.Message.Message
			);
			Assert.Equal(s_peer.ToString(), sent.Message.Chat);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SetDisappearingTimer_ADirectChatRepeatedWithItsId_SendsTheSettingOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "disappearing-timer-repeat";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var id = MessageId.New();

		await client.SetDisappearingTimerAsync(s_peer, TimeSpan.FromDays(7), id, ct);
		// A retry after an unknown outcome repeats the id; the send record answers it, and nothing goes out again.
		await client.SetDisappearingTimerAsync(s_peer, TimeSpan.FromDays(7), id, ct);
		await client.SetDisappearingTimerAsync(s_peer, TimeSpan.Zero, cancellationToken: ct);

		var first = await NextMessageAsync(Session, 0, ct);
		var next = await NextMessageAsync(Session, first.Index, ct);
		Assert.Equal((id.Value, 604_800u), (first.Message.Id, first.Message.Message.ProtocolMessage.EphemeralExpiration));
		Assert.NotEqual(id.Value, next.Message.Id, StringComparer.Ordinal);
		Assert.Equal(0u, next.Message.Message.ProtocolMessage.EphemeralExpiration);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_TheOtherSideSetsTheTimerThenTurnsItOff_TheHandlerGetsTheChatsNewTimer()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "disappearing-timer-received";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);
		var watch = StateWatch.Start(client, ct);
		await using (watch)
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());

		foreach (var (id, seconds) in new[] { ("DT1", 86_400u), ("DT2", 0u) })
		{
			_ = await fixture.Fake.DeliverMessageAsync(
				new DeliverMessageRequest
				{
					SessionId = Session,
					Id = id,
					Chat = s_peer.ToString(),
					Sender = s_peer.ToString(),
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
					Message = new E2E.Message
					{
						ProtocolMessage = new E2E.ProtocolMessage { Type = ProtocolType.EphemeralSetting, EphemeralExpiration = seconds, EphemeralSettingTimestamp = 1_790_000_000 },
					},
				},
				cancellationToken: ct
			);
		}

		var expected = MessageChecks.Received(
			SessionId.From(Session),
			MessageId.From("DT1"),
			new MessageSource { Chat = s_peer, Sender = s_peer, SenderAlt = null, RecipientAlt = null, IsFromMe = false },
			pushName: null,
			Deliveries.At,
			new DisappearingTimerContent { Timer = TimeSpan.FromDays(1) }
		);
		Assert.Equal(expected, await received.NextAsync(ct));
		Assert.Equal(expected with { Id = MessageId.From("DT2"), Content = new DisappearingTimerContent { Timer = TimeSpan.Zero } }, await received.NextAsync(ct));
	}

	/// <summary>The session's next message node after the entry <paramref name="after"/>, past the receipts and acks.</summary>
	private async Task<Sent> NextMessageAsync(string session, ulong after, CancellationToken ct)
	{
		while (true)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			if (next.Message is not null)
				return next;
			after = next.Index;
		}
	}
}
