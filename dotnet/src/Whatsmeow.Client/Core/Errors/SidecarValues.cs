namespace Whatsmeow.Core.Errors;

/// <summary>
/// Reads the values the sidecar sends, in a response or an event. One this client cannot read is the library's fault,
/// never the caller's invalid argument: a <see cref="WhatsmeowException"/> with <see cref="WhatsmeowErrorCode.Internal"/>
/// that names the field, as the contract's <c>Message.field</c>, and never its value, which can hold a phone number
/// (ARCHITECTURE §8.7, §8.8).
/// </summary>
internal static class SidecarValues
{
	public static Jid Jid(string value, string field) => Whatsmeow.Jid.TryParse(value, out var jid) ? jid : Unreadable<Jid>(field);

	public static MessageId MessageId(string value, string field) =>
		Whatsmeow.MessageId.TryFrom(value, out var id) ? id : Unreadable<MessageId>(field);

	public static SessionId SessionId(string value, string field) =>
		Whatsmeow.SessionId.TryFrom(value, out var id) ? id : Unreadable<SessionId>(field);

	public static long Length(ulong value, string field) => value <= long.MaxValue ? (long)value : Unreadable<long>(field);

	/// <summary>Always throws: the field holds a value this client does not know, such as an unset result.</summary>
	public static T Unreadable<T>(string field)
	{
		WhatsmeowException.ThrowUnreadable(field);
		return default;
	}
}
