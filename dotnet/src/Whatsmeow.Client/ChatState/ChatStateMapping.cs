using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.Protocol.V1;
using Change = Whatsmeow.Protocol.V1.ChatStateEvent.ChangeOneofCase;

namespace Whatsmeow.ChatState;

/// <summary>chatstate.proto as the public records, and the requests the client sends.</summary>
internal static class ChatStateMapping
{
	public static ChatMute ToMute(DateTimeOffset? until) => new() { Muted = true, Until = until is { } end ? Timestamp.FromDateTimeOffset(end) : null };

	/// <summary>A star of the message: the sidecar names the sender only for a group message from someone else.</summary>
	public static MessageStar ToStar(MessageId id, Events.MessageSource source, bool starred) =>
		new()
		{
			MessageId = id.Value,
			IsFromMe = source.IsFromMe,
			Sender = source.Sender.ToString(),
			Starred = starred,
		};

	/// <summary>The event of a ChatStateEvent, or null for a change this client does not know.</summary>
	/// <exception cref="WhatsmeowValidationException">The chat or a star's message id is missing or malformed: the event is unusable.</exception>
	public static ChatStateChanged? ToEvent(SessionId sessionId, ChatStateEvent chatState) =>
		ToChange(chatState) is { } change
			? new ChatStateChanged
			{
				SessionId = sessionId,
				Chat = Jid.Parse(chatState.Chat),
				Timestamp = EventMapping.ToTime(chatState.ChangeTime),
				FromFullSync = chatState.FromFullSync,
				Change = change,
			}
			: null;

	private static ChatStateChange? ToChange(ChatStateEvent chatState) =>
		chatState.ChangeCase switch
		{
			Change.Mute => new ChatStateChange.Mute
			{
				IsMuted = chatState.Mute.Muted,
				Until = chatState.Mute.Until is null ? null : EventMapping.ToTime(chatState.Mute.Until),
			},
			Change.Pin => new ChatStateChange.Pin { IsPinned = chatState.Pin.Pinned },
			Change.Archive => new ChatStateChange.Archive { IsArchived = chatState.Archive.Archived },
			Change.Read => new ChatStateChange.Read { IsRead = chatState.Read.Read },
			Change.Star => new ChatStateChange.Star
			{
				MessageId = MessageId.From(chatState.Star.MessageId),
				Sender = Jid.TryParse(chatState.Star.Sender, out var sender) ? sender : null,
				IsFromMe = chatState.Star.IsFromMe,
				IsStarred = chatState.Star.Starred,
			},
			Change.None => null,
			_ => null,
		};
}
