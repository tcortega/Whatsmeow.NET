using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Presence;
using Whatsmeow.Presence;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Presence (#32) between the live account and the peer, through real WhatsApp: the peer's presence and typing reach a
/// subscribed live session, checked with the fake-mode tests' own checks (<see cref="PresenceChecks"/>), and whether they
/// still come while the live session is unavailable is recorded. Both end unavailable, as a companion that never said
/// otherwise is to the phone.
/// </summary>
[Collection(nameof(Live))]
public sealed class LivePresenceTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>
	/// Whether this run subscribed to the peer while available, before the unavailable test: WhatsApp may keep that
	/// subscription [unknown], so the unavailable test logs it beside what it saw. The live tests run one at a time.
	/// </summary>
	private static bool s_subscribedWhileAvailable;

	/// <summary>How long the unavailable test watches for what still comes.</summary>
	private static readonly TimeSpan s_unavailableWatch = TimeSpan.FromSeconds(30);

	/// <summary>The live session, available, subscribes to the peer: the peer's presence comes, then its typing and its pause.</summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_SubscribedToThePeer_ItsPresenceArrives_ThenItsTypingAndItsPause()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (account, _) = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		using var typing = new LiveEvents<ChatPresenceChanged>(client, c => LiveChecks.Names(peerAccount, c.Source.Sender));
		try
		{
			await client.SendPresenceAsync(PresenceState.Available, ct);
			var online = client.WaitForAsync<PresenceChanged>(p => LiveChecks.Names(peerAccount, p.From) && p.State is PresenceState.Available, LiveChecks.Watch, ct);
			await client.SubscribePresenceAsync(peerAccount.Chat, ct);
			s_subscribedWhileAvailable = true;
			await other.SendPresenceAsync(PresenceState.Available, ct);
			var presence = await LiveChecks.WithinAsync(online, "the peer's presence");
			live.Write($"live: the peer's presence came from a {LiveChecks.Form(presence.From)}, last seen {(presence.LastSeen is null ? "none" : "set")}");

			await other.SendChatPresenceAsync(account, ChatPresenceState.Typing, ct);
			await other.SendChatPresenceAsync(account, ChatPresenceState.Paused, ct);
			var seen = await typing.NextAsync(2, LiveChecks.Watch, ct);
			live.Write($"live: the peer's typing came in a {LiveChecks.Form(seen.Count > 0 ? seen[0].Source.Chat : null)} chat: {string.Join(", ", seen.Select(c => c.State))}");

			Assert.NotEmpty(seen);
			LiveChecks.Redacting(() => PresenceChecks.ValidateTypingThenPaused(seen, client.SessionId, seen[0].Source));
		}
		finally
		{
			// The live session first: while it is available, the owner's phone holds back its notifications.
			await client.SendPresenceAsync(PresenceState.Unavailable, CancellationToken.None);
			await other.SendPresenceAsync(PresenceState.Unavailable, CancellationToken.None);
		}
	}

	/// <summary>
	/// The live session subscribed, then unavailable: whether the peer's presence and its typing still come (the
	/// [unknown] ARCHITECTURE §14 keeps) goes to the log, each with how long it took, or that it did not come within 30 s.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_TheLiveSessionIsUnavailable_WhetherThePeersPresenceAndTypingStillComeIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (account, _) = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		using var typing = new LiveEvents<ChatPresenceChanged>(client, c => LiveChecks.Names(peerAccount, c.Source.Sender));
		live.Write($"live: this run {(s_subscribedWhileAvailable ? "already subscribed to the peer while available" : "had not subscribed to the peer before")}");
		try
		{
			await client.SubscribePresenceAsync(peerAccount.Chat, ct);
			await client.SendPresenceAsync(PresenceState.Unavailable, ct);
			await other.SendPresenceAsync(PresenceState.Unavailable, ct);

			var started = LiveClock.Time.GetTimestamp();
			var online = LiveChecks.OrNullAsync(
				client.WaitForAsync<PresenceChanged>(p => LiveChecks.Names(peerAccount, p.From) && p.State is PresenceState.Available, s_unavailableWatch, ct)
			);
			await other.SendPresenceAsync(PresenceState.Available, ct);
			var presence = await online;
			live.Write(
				presence is null
					? $"live: unavailable, the peer's presence did not come within {s_unavailableWatch.TotalSeconds:F0} s"
					: $"live: unavailable, the peer's presence came {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s after it went online"
			);

			started = LiveClock.Time.GetTimestamp();
			await other.SendChatPresenceAsync(account, ChatPresenceState.Typing, ct);
			var seen = await typing.NextAsync(1, s_unavailableWatch, ct);
			live.Write(
				seen.Count == 0
					? $"live: unavailable, the peer's typing did not come within {s_unavailableWatch.TotalSeconds:F0} s"
					: $"live: unavailable, the peer's typing came {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s after it began"
			);
		}
		finally
		{
			await client.SendPresenceAsync(PresenceState.Unavailable, CancellationToken.None);
			await other.SendChatPresenceAsync(account, ChatPresenceState.Paused, CancellationToken.None);
			await other.SendPresenceAsync(PresenceState.Unavailable, CancellationToken.None);
		}
	}
}
