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

	/// <summary>
	/// The peer available, the live session, available, subscribes to it: a presence of the peer comes, then its typing
	/// and its pause. Which state that presence carries is WhatsApp's to decide and goes to the log: live, it was
	/// unavailable with no last seen, as for a contact who hides it (the fake-mode hidden test), and no available came
	/// within 60 s [observed: #36 live run of 2026-11-04 at dfa2e57]. Why WhatsApp hid the peer's presence from the live
	/// account, its privacy setting or the privacy token whatsmeow subscribes without (ARCHITECTURE §14), is [unknown].
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_SubscribedToThePeer_ItsPresenceArrives_ThenItsTypingAndItsPause()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (account, _) = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		using var typing = new LiveEvents<ChatPresenceChanged>(client, c => LiveChecks.Names(peerAccount, c.Source.Sender));
		using var everyPresence = new LiveEvents<PresenceChanged>(client, p => LiveChecks.Names(peerAccount, p.From));
		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await other.SendPresenceAsync(PresenceState.Available, ct).ConfigureAwait(false);
				await client.SendPresenceAsync(PresenceState.Available, ct).ConfigureAwait(false);
				await client.SubscribePresenceAsync(peerAccount.Chat, ct).ConfigureAwait(false);
				s_subscribedWhileAvailable = true;
				var first = await everyPresence.NextAsync(1, LiveChecks.Watch, ct).ConfigureAwait(false);
				var presences = first.Concat(await everyPresence.NextAsync(int.MaxValue, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)).ToList();
				live.Write(
					$"live: the peer's presences after the subscribe: {(presences.Count == 0 ? "none" : string.Join(", ", presences.Select(p => $"{p.State} from a {LiveChecks.Form(p.From)}, last seen {(p.LastSeen is null ? "none" : "set")}")))}"
				);
				Assert.True(presences.Count > 0, $"no presence of the peer came within {LiveChecks.Watch.TotalSeconds:F0} s");

				await other.SendChatPresenceAsync(account, ChatPresenceState.Typing, ct).ConfigureAwait(false);
				await other.SendChatPresenceAsync(account, ChatPresenceState.Paused, ct).ConfigureAwait(false);
				var seen = await typing.NextAsync(2, LiveChecks.Watch, ct).ConfigureAwait(false);
				live.Write($"live: the peer's typing came in a {LiveChecks.Form(seen.Count > 0 ? seen[0].Source.Chat : null)} chat: {string.Join(", ", seen.Select(c => c.State))}");

				Assert.NotEmpty(seen);
				LiveChecks.Redacting(() => PresenceChecks.ValidateTypingThenPaused(seen, client.SessionId, seen[0].Source));
			},
			// The live session first: while it is available, the owner's phone holds back its notifications.
			("make the live session unavailable", t => client.SendPresenceAsync(PresenceState.Unavailable, t)),
			("make the peer unavailable", t => other.SendPresenceAsync(PresenceState.Unavailable, t))
		);
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
		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await client.SubscribePresenceAsync(peerAccount.Chat, ct).ConfigureAwait(false);
				await client.SendPresenceAsync(PresenceState.Unavailable, ct).ConfigureAwait(false);
				await other.SendPresenceAsync(PresenceState.Unavailable, ct).ConfigureAwait(false);

				var started = LiveClock.Time.GetTimestamp();
				var online = LiveChecks.OrNullAsync(
					client.WaitForAsync<PresenceChanged>(p => LiveChecks.Names(peerAccount, p.From) && p.State is PresenceState.Available, s_unavailableWatch, ct)
				);
				await other.SendPresenceAsync(PresenceState.Available, ct).ConfigureAwait(false);
				var presence = await online.ConfigureAwait(false);
				live.Write(
					presence is null
						? $"live: unavailable, the peer's presence did not come within {s_unavailableWatch.TotalSeconds:F0} s"
						: $"live: unavailable, the peer's presence came {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s after it went online"
				);

				started = LiveClock.Time.GetTimestamp();
				await other.SendChatPresenceAsync(account, ChatPresenceState.Typing, ct).ConfigureAwait(false);
				var seen = await typing.NextAsync(1, s_unavailableWatch, ct).ConfigureAwait(false);
				live.Write(
					seen.Count == 0
						? $"live: unavailable, the peer's typing did not come within {s_unavailableWatch.TotalSeconds:F0} s"
						: $"live: unavailable, the peer's typing came {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s after it began"
				);
			},
			("make the live session unavailable", t => client.SendPresenceAsync(PresenceState.Unavailable, t)),
			("pause the peer's typing", t => other.SendChatPresenceAsync(account, ChatPresenceState.Paused, t)),
			("make the peer unavailable", t => other.SendPresenceAsync(PresenceState.Unavailable, t))
		);
	}
}
