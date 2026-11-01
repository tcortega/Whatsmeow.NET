using System.Collections.Immutable;
using Whatsmeow.Protocol.V1;
using Change = Whatsmeow.Protocol.V1.ContactEvent.ChangeOneofCase;
using PictureResult = Whatsmeow.Protocol.V1.GetProfilePictureResponse.ResultOneofCase;

namespace Whatsmeow.Contacts;

/// <summary>contact.proto as the public records.</summary>
internal static class ContactMapping
{
	public static ImmutableArray<PhoneNumberCheck> ToChecks(IsOnWhatsAppResponse response) =>
		[
			.. response.Results.Select(check => new PhoneNumberCheck
			{
				PhoneNumber = check.PhoneNumber,
				IsOnWhatsApp = check.OnWhatsapp,
				Jid = ToJid(check.Jid),
				Lid = ToJid(check.Lid),
			}),
		];

	public static ImmutableArray<UserInfo> ToUsers(GetUserInfoResponse response) =>
		[
			.. response.Users.Select(user => new UserInfo
			{
				Jid = Jid.Parse(user.Jid),
				About = NullIfEmpty(user.About),
				VerifiedBusinessName = NullIfEmpty(user.VerifiedBusinessName),
				PictureId = NullIfEmpty(user.PictureId),
			}),
		];

	public static ProfilePicture ToPicture(GetProfilePictureResponse response) =>
		response.ResultCase switch
		{
			PictureResult.Picture => new ProfilePicture.Set
			{
				Url = new Uri(response.Picture.Url, UriKind.RelativeOrAbsolute),
				Id = response.Picture.Id,
			},
			PictureResult.Hidden => new ProfilePicture.Hidden(),
			PictureResult.None or PictureResult.None_ => new ProfilePicture.None(),
			_ => new ProfilePicture.None(),
		};

	public static ImmutableArray<ResolvedJid> ToResolved(ResolveJidsResponse response) =>
		[.. response.Results.Select(result => new ResolvedJid { Jid = Jid.Parse(result.Jid), Alt = ToJid(result.Alt) })];

	public static ImmutableArray<Contact> ToContacts(ListContactsResponse response) =>
		[
			.. response.Contacts.Select(contact => new Contact
			{
				Jid = Jid.Parse(contact.Jid),
				SavedName = NullIfEmpty(contact.SavedName),
				PushName = NullIfEmpty(contact.PushName),
				BusinessName = NullIfEmpty(contact.BusinessName),
			}),
		];

	public static ImmutableArray<BlockedUser> ToBlocked(GetBlocklistResponse response) =>
		[.. response.Users.Select(user => new BlockedUser { Jid = Jid.Parse(user.Jid), JidAlt = ToJid(user.JidAlt) })];

	/// <summary>The event of a ContactEvent, or null for a change this client does not know.</summary>
	/// <exception cref="WhatsmeowValidationException">The JID is missing or malformed: the event is unusable.</exception>
	public static ContactUpdated? ToEvent(SessionId sessionId, ContactEvent contact) =>
		ToChange(contact) is { } change
			? new ContactUpdated
			{
				SessionId = sessionId,
				Jid = Jid.Parse(contact.Jid),
				JidAlt = ToJid(contact.JidAlt),
				Change = change,
			}
			: null;

	private static ContactChange? ToChange(ContactEvent contact) =>
		contact.ChangeCase switch
		{
			Change.PushName => new ContactChange.PushName { Name = contact.PushName.Name },
			Change.BusinessName => new ContactChange.BusinessName { Name = contact.BusinessName.Name },
			Change.Picture => new ContactChange.Picture { Id = NullIfEmpty(contact.Picture.Id) },
			Change.About => new ContactChange.About { Text = contact.About.Text },
			Change.None => null,
			_ => null,
		};

	/// <summary>An empty or malformed JID is unknown.</summary>
	private static Jid? ToJid(string value) => Jid.TryParse(value, out var jid) ? jid : null;

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
