using System.Collections.Immutable;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.LiveTests;

/// <summary>
/// What the live tests write or fail with, redacted: <see cref="LogRedaction"/> catches a JID's user part and the test
/// number, and every user part of the two accounts, their phone numbers and their LIDs, goes too, wherever it is: a
/// mention's text carries a bare <c>@user</c>, which <see cref="LogRedaction"/> cannot tell from other digits.
/// </summary>
internal static class LiveRedaction
{
	private static ImmutableHashSet<string> s_users = [];

	/// <summary>Redacts <paramref name="jid"/>'s user part from now on; <see cref="LiveSidecar.AccountOfAsync"/> adds both accounts'.</summary>
	public static void Add(Jid? jid)
	{
		if (jid is { User.Length: > 0 } known)
			_ = ImmutableInterlocked.Update(ref s_users, users => users.Add(known.User));
	}

	/// <summary><paramref name="text"/> with every known user part and every JID user part replaced.</summary>
	public static string Redact(string text)
	{
		// The longest first, so a user part that holds another is not left half shown.
		foreach (var user in s_users.OrderByDescending(u => u.Length))
			text = text.Replace(user, LogRedaction.Redacted, StringComparison.Ordinal);
		return LogRedaction.Redact(text, LiveSettings.PhoneNumber);
	}
}
