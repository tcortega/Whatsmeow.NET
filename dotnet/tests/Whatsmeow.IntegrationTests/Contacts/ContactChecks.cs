using System.Collections.Immutable;
using Whatsmeow.Contacts;

namespace Whatsmeow.IntegrationTests.Contacts;

/// <summary>
/// What the blocklist must read after a block and after an unblock. The fake-mode contacts tests and
/// <c>Whatsmeow.LiveTests</c> check a run with these same assertions (docs/guidelines/tests.md Live WhatsApp).
/// </summary>
internal static class ContactChecks
{
	/// <summary>The blocked user alone, listed by its LID with its phone number beside it.</summary>
	public static void ValidateBlocked(ImmutableArray<BlockedUser> blocklist, Jid lid, Jid phoneNumber) =>
		Assert.Equal([new BlockedUser { Jid = lid, JidAlt = phoneNumber }], blocklist);

	/// <summary>Nobody blocked once the user is unblocked.</summary>
	public static void ValidateUnblocked(ImmutableArray<BlockedUser> blocklist) => Assert.Empty(blocklist);
}
