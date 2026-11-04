using Whatsmeow.Groups;

namespace Whatsmeow.IntegrationTests.Groups;

/// <summary>
/// The records a group change must be. The fake-mode groups tests and <c>Whatsmeow.LiveTests</c> build what they expect
/// with these same functions and compare it whole (docs/guidelines/tests.md Live WhatsApp). Who made the change, in the
/// forms WhatsApp named them, and its time are passed in: the fake's are fixed, and a live test reads them off the
/// event after checking that its sender is the account that made it.
/// </summary>
internal static class GroupChecks
{
	/// <summary>The changes <paramref name="sender"/> made to <paramref name="group"/>, as one event.</summary>
	public static GroupInfoChanged Changed(SessionId session, Jid group, Jid? sender, Jid? senderAlt, DateTimeOffset timestamp, params GroupChange[] changes) =>
		new()
		{
			SessionId = session,
			Group = group,
			Sender = sender,
			SenderAlt = senderAlt,
			Timestamp = timestamp,
			Changes = [.. changes],
		};

	/// <summary>What an admin sets, in order, and the change each one is to a member: the name, the topic, announce, locked.</summary>
	public static GroupChange[] Settings(string name, string topic) =>
		[
			new GroupChange.Renamed { Name = name },
			new GroupChange.TopicChanged { Topic = topic },
			new GroupChange.AnnounceChanged { IsAnnounce = true },
			new GroupChange.LockedChanged { IsLocked = true },
		];

	/// <summary>Sets <see cref="Settings"/> on <paramref name="group"/>, as <paramref name="admin"/>, in their order.</summary>
	public static async Task SetSettingsAsync(IWhatsAppClient admin, Jid group, string name, string topic, CancellationToken cancellationToken)
	{
		await admin.SetGroupNameAsync(group, name, cancellationToken).ConfigureAwait(false);
		await admin.SetGroupTopicAsync(group, topic, cancellationToken).ConfigureAwait(false);
		await admin.SetGroupAnnounceAsync(group, announce: true, cancellationToken).ConfigureAwait(false);
		await admin.SetGroupLockedAsync(group, locked: true, cancellationToken).ConfigureAwait(false);
	}
}
