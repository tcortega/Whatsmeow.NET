using System.Text.RegularExpressions;
using Whatsmeow.Groups;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A run's group, with the peer its only other member: the live session creates it the first time a test of a class
/// that has it as its class fixture needs it, so one per such class, closed after that class. Each test leaves it as it
/// found it, and one that took the peer out adds it back (<see cref="WithPeerAsync"/>). At the end the live session
/// removes the peer and leaves, the closest WhatsApp has to deleting a group: no other number is ever added. Before it
/// creates one, it does the same to every group an aborted run left: one the live account owns, whose name is a run's,
/// with no member but the two accounts.
/// </summary>
public sealed partial class LiveGroup(LiveSidecar live, LivePeer peer) : IAsyncLifetime
{
	/// <summary>How long the connect before the close at the end may take.</summary>
	private static readonly TimeSpan s_connectBound = TimeSpan.FromMinutes(3);

	private readonly SemaphoreSlim _creating = new(1, 1);
	private Creation? _creation;

	/// <summary>What the creation answered and what each side got, recorded once.</summary>
	public sealed record Creation
	{
		/// <summary>The group's name, unique to the run.</summary>
		public required string Name { get; init; }

		/// <summary>The create answer.</summary>
		public required CreatedGroup Created { get; init; }

		/// <summary>The group's info read right after the create, before any test changed it.</summary>
		public required GroupInfo InfoAfter { get; init; }

		/// <summary>The creator's <see cref="GroupJoined"/>, null when none came within <see cref="LiveChecks.Watch"/>.</summary>
		public required GroupJoined? CreatorJoined { get; init; }

		/// <summary>The peer's <see cref="GroupJoined"/>, null when none came within <see cref="LiveChecks.Watch"/>.</summary>
		public required GroupJoined? PeerJoined { get; init; }
	}

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>The group, created on the first call with the peer, each answer written to the log.</summary>
	public async Task<Creation> CreatedAsync(CancellationToken cancellationToken)
	{
		await _creating.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_creation is not null)
				return _creation;

			var client = await live.LiveAsync(cancellationToken).ConfigureAwait(false);
			var other = await peer.ConnectedAsync(live, cancellationToken).ConfigureAwait(false);
			var (to, _) = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
			var liveAccount = await live.AccountAsync(cancellationToken).ConfigureAwait(false);
			var peerAccount = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
			var leftovers = (await client.GetGroupsAsync(cancellationToken).ConfigureAwait(false)).Where(g => IsLeftover(g, liveAccount, peerAccount)).ToList();
			foreach (var leftover in leftovers)
				await CloseAsync(client, leftover.Jid, peerAccount).ConfigureAwait(false);
			if (leftovers.Count > 0)
				live.Write($"live: closed {leftovers.Count} groups an earlier run left");

			var name = $"wmn live {LiveClock.Time.GetUtcNow().ToUnixTimeSeconds()}";
			var creatorJoined = LiveChecks.OrNullAsync(client.WaitForAsync<GroupJoined>(j => j.Group.Name == name, LiveChecks.Watch, cancellationToken));
			var peerJoined = LiveChecks.OrNullAsync(other.WaitForAsync<GroupJoined>(j => j.Group.Name == name, LiveChecks.Watch, cancellationToken));

			var created = await client.CreateGroupAsync(name, [to], cancellationToken).ConfigureAwait(false);
			// Recorded before anything can fail, so the leave at the end finds it.
			_creation = new Creation
			{
				Name = name,
				Created = created,
				InfoAfter = created.Group,
				CreatorJoined = null,
				PeerJoined = null,
			};
			live.Write(
				$"live: created a group; the answer lists {string.Join(", ", created.Participants.Select(p => $"a {LiveChecks.Form(p.Jid)} ({p.Outcome.GetType().Name})"))}, "
					+ $"and its members as {Members(created.Group)}, the owner a {LiveChecks.Form(created.Group.Owner)}"
			);

			var infoAfter = await client.GetGroupInfoAsync(created.Group.Jid, cancellationToken).ConfigureAwait(false);
			var creator = await creatorJoined.ConfigureAwait(false);
			var joined = await peerJoined.ConfigureAwait(false);
			live.Write(
				$"live: the creator {(creator is null ? $"got no GroupJoined within {LiveChecks.Watch.TotalSeconds:F0} s" : "got GroupJoined")}, "
					+ $"the peer {(joined is null ? $"got no GroupJoined within {LiveChecks.Watch.TotalSeconds:F0} s" : $"got GroupJoined from a {LiveChecks.Form(joined.Sender)}")}"
			);
			_creation = _creation with { InfoAfter = infoAfter, CreatorJoined = creator, PeerJoined = joined };
			return _creation;
		}
		finally
		{
			_ = _creating.Release();
		}
	}

	/// <summary>The group's JID, with the peer a member: when a test took it out, the live session, its admin, adds it back.</summary>
	public async Task<Jid> WithPeerAsync(CancellationToken cancellationToken)
	{
		var group = (await CreatedAsync(cancellationToken).ConfigureAwait(false)).Created.Group.Jid;
		var client = await live.LiveAsync(cancellationToken).ConfigureAwait(false);
		_ = await peer.ConnectedAsync(live, cancellationToken).ConfigureAwait(false);
		var account = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
		var info = await client.GetGroupInfoAsync(group, cancellationToken).ConfigureAwait(false);
		if (info.Participants.Any(p => LiveChecks.Names(account, p.Jid)))
			return group;

		var added = await client.AddParticipantsAsync(group, [account.Chat], cancellationToken).ConfigureAwait(false);
		live.Write($"live: added the peer back to the group: {string.Join(", ", added.Select(r => r.Outcome is ParticipantOutcome.Refused refused ? $"refused with {refused.Code}" : r.Outcome.GetType().Name))}");
		Assert.True(added.Length == 1 && added[0].Outcome is ParticipantOutcome.Done, "the peer could not be added back to the group");
		return group;
	}

	/// <summary>The name a test sets on the group named <paramref name="name"/>, with <paramref name="suffix"/>: <c>crew</c> or <c>not mine</c> (<see cref="RunName"/>).</summary>
	public static string RenamedBy(string name, string suffix) => $"{name} {suffix}";

	/// <summary>Each member's address forms and role, for the log: the addressing mode WhatsApp chose shows there.</summary>
	public static string Members(GroupInfo info) => string.Join(", ", info.Participants.Select(p => $"{LiveChecks.Form(p.Jid)} with {LiveChecks.Form(p.JidAlt)} ({p.Role})"));

	/// <summary>
	/// Closes the run's group (<see cref="CloseAsync"/>); nothing it throws stops the fixture's own disposal, and the
	/// connect before it is bounded, so a WhatsApp that never answers cannot hold the run.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		try
		{
			if (_creation is { Created.Group.Jid: var group })
			{
				using var bound = LiveClock.Bound(s_connectBound, CancellationToken.None);
				var client = await live.LiveAsync(bound.Token).ConfigureAwait(false);
				await CloseAsync(client, group, await peer.AccountAsync(bound.Token).ConfigureAwait(false)).ConfigureAwait(false);
			}
		}
#pragma warning disable CA1031 // Justification: a fixture's disposal must not fail the run; what failed goes to the log.
		catch (Exception e)
#pragma warning restore CA1031
		{
			live.Write($"live: could not close the run's group at the end: {e.GetType().Name}");
		}
		finally
		{
			_creating.Dispose();
		}
	}

	/// <summary>
	/// Removes the peer from <paramref name="group"/>, if it is a member, then leaves it: each step bounded, the leave
	/// whatever the removal threw, and a failure of either goes to the log, not up (<see cref="LiveRestore.RunAsync"/>).
	/// </summary>
	private async Task CloseAsync(IWhatsAppClient client, Jid group, (Jid Chat, Jid? Lid) peerAccount)
	{
		async Task RemovePeerAsync(CancellationToken token)
		{
			var info = await client.GetGroupInfoAsync(group, token).ConfigureAwait(false);
			var members = info.Participants.Where(p => LiveChecks.Names(peerAccount, p.Jid)).Select(p => p.Jid).ToList();
			if (members.Count > 0)
				_ = await client.RemoveParticipantsAsync(group, members, token).ConfigureAwait(false);
		}

		var failed = await LiveRestore
			.RunAsync(live, ("remove the peer from a group", RemovePeerAsync), ("leave a group", token => client.LeaveGroupAsync(group, token)))
			.ConfigureAwait(false);
		if (failed.Count == 0)
			live.Write("live: removed the peer from a group and left it");
	}

	/// <summary>
	/// Whether <paramref name="info"/> is a group an earlier run of this suite left: one the live account owns, with a
	/// name a run gives it or a test sets (<see cref="RunName"/>), and no member but the live account and the peer, the
	/// only number a run adds.
	/// </summary>
	private static bool IsLeftover(GroupInfo info, (Jid Chat, Jid? Lid) liveAccount, (Jid Chat, Jid? Lid) peerAccount) =>
		RunName().IsMatch(info.Name)
		&& info.Owner is { } owner
		&& LiveChecks.Names(liveAccount, owner)
		&& info.Participants.All(p => LiveChecks.Names(liveAccount, p.Jid) || LiveChecks.Names(peerAccount, p.Jid));

	/// <summary>
	/// A run's group name, as <see cref="CreatedAsync"/> makes it, and every name a test sets on it, however many a killed
	/// run left on top of each other: the settings test adds <c> crew</c>, and the non-admin test's peer tries
	/// <c> not mine</c> (<see cref="RenamedBy"/>).
	/// </summary>
	[GeneratedRegex("^wmn live [0-9]+( crew| not mine)*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex RunName();
}
