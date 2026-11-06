using System.Collections.Immutable;
using Whatsmeow.Contacts;

namespace Whatsmeow.IntegrationTests.Contacts;

/// <summary>
/// The records a contact change must be, and what the blocklist must read after a block and after an unblock. The
/// fake-mode contacts tests and <c>Whatsmeow.LiveTests</c> build what they expect with these same functions and compare
/// it whole (docs/guidelines/tests.md Live WhatsApp). What WhatsApp names on the way, the forms of the user and a name it
/// alone knows, is passed in: the fake's are fixed, and a live test reads them off the message or the answer that caused
/// the change after checking that it names the right account.
/// </summary>
internal static class ContactChecks
{
	/// <summary>A new push name, carried by a message from <paramref name="jid"/>, with its other form when known.</summary>
	public static ContactUpdated PushName(SessionId session, Jid jid, Jid? jidAlt, string name) =>
		new() { SessionId = session, Jid = jid, JidAlt = jidAlt, Change = new ContactChange.PushName { Name = name } };

	/// <summary>A verified business name, stored by reading the business's profile by its phone number.</summary>
	public static ContactUpdated BusinessName(SessionId session, Jid phoneNumber, string name) =>
		new() { SessionId = session, Jid = phoneNumber, JidAlt = null, Change = new ContactChange.BusinessName { Name = name } };

	/// <summary>A picture set on a user or a group, or removed when <paramref name="id"/> is null.</summary>
	public static ContactUpdated Picture(SessionId session, Jid jid, string? id) =>
		new() { SessionId = session, Jid = jid, JidAlt = null, Change = new ContactChange.Picture { Id = id } };

	/// <summary>A new about text.</summary>
	public static ContactUpdated About(SessionId session, Jid jid, string text) =>
		new() { SessionId = session, Jid = jid, JidAlt = null, Change = new ContactChange.About { Text = text } };

	/// <summary>The blocked user alone, listed by its LID with its phone number beside it.</summary>
	public static void ValidateBlocked(ImmutableArray<BlockedUser> blocklist, Jid lid, Jid phoneNumber) =>
		Assert.Equal([new BlockedUser { Jid = lid, JidAlt = phoneNumber }], blocklist);

	/// <summary>Nobody blocked once the user is unblocked.</summary>
	public static void ValidateUnblocked(ImmutableArray<BlockedUser> blocklist) => Assert.Empty(blocklist);
}
