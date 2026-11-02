using Whatsmeow.Groups;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The run's one group, with the peer its only other member: the live session creates it the first time a group test
/// needs it, so a run creates one group however many tests use it. Each test leaves it as it found it, and one that took
/// the peer out has it join again (<see cref="WithPeerAsync"/>). At the end the live session removes the peer and
/// leaves, the closest WhatsApp has to deleting a group: no other number is ever added.
/// </summary>
public sealed class LiveGroup(LiveSidecar live, LivePeer peer) : IAsyncLifetime
{
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

	/// <summary>The group's JID, with the peer a member: when a test took it out, the peer joins again by the invite link.</summary>
	public async Task<Jid> WithPeerAsync(CancellationToken cancellationToken)
	{
		var group = (await CreatedAsync(cancellationToken).ConfigureAwait(false)).Created.Group.Jid;
		var client = await live.LiveAsync(cancellationToken).ConfigureAwait(false);
		var other = await peer.ConnectedAsync(live, cancellationToken).ConfigureAwait(false);
		var account = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
		var info = await client.GetGroupInfoAsync(group, cancellationToken).ConfigureAwait(false);
		if (info.Participants.Any(p => LiveChecks.Names(account, p.Jid)))
			return group;

		_ = await other.JoinGroupWithLinkAsync(await client.GetGroupInviteLinkAsync(group, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
		live.Write("live: the peer joined the group again by its invite link");
		return group;
	}

	/// <summary>Each member's address forms and role, for the log: the addressing mode WhatsApp chose shows there.</summary>
	public static string Members(GroupInfo info) => string.Join(", ", info.Participants.Select(p => $"{LiveChecks.Form(p.Jid)} with {LiveChecks.Form(p.JidAlt)} ({p.Role})"));

	/// <summary>Removes the peer, if it is still a member, then leaves, each step on its own so a failure leaves nothing undone that can be done.</summary>
	public async ValueTask DisposeAsync()
	{
		if (_creation is { Created.Group.Jid: var group })
		{
			var client = await live.LiveAsync(CancellationToken.None).ConfigureAwait(false);
			var account = await peer.AccountAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				var info = await client.GetGroupInfoAsync(group, CancellationToken.None).ConfigureAwait(false);
				var members = info.Participants.Where(p => LiveChecks.Names(account, p.Jid)).Select(p => p.Jid).ToList();
				if (members.Count > 0)
					_ = await client.RemoveParticipantsAsync(group, members, CancellationToken.None).ConfigureAwait(false);
			}
			catch (WhatsmeowException e)
			{
				live.Write($"live: could not remove the peer from the group at the end: {e.ErrorCode}");
			}

			await client.LeaveGroupAsync(group, CancellationToken.None).ConfigureAwait(false);
			live.Write("live: removed the peer from the group and left it");
		}

		_creating.Dispose();
	}
}
