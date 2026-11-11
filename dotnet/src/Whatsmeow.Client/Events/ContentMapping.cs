using System.Collections.Immutable;
using Google.Protobuf;
using Whatsmeow.Core.Errors;
using Whatsmeow.Messaging;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageTarget = Whatsmeow.Protocol.V1.MessageTarget;
using PollVote = Whatsmeow.Protocol.V1.PollVote;
using ProtocolType = Whatsmeow.Proto.WAWebProtobufsE2E.ProtocolMessage.Types.Type;

namespace Whatsmeow.Events;

/// <summary>
/// A received message as its <see cref="MessageContent"/> case. It maps field by field and never throws: a field the
/// message lacks is unknown (null, or empty bytes), so the content still arrives.
/// </summary>
internal static class ContentMapping
{
	/// <param name="message">The message as whatsmeow unwrapped it.</param>
	/// <param name="viewOnce">True when whatsmeow unwrapped it from a view-once wrapper.</param>
	/// <param name="target">
	/// The message a reaction, an edit or a revoke names, as the sidecar read its key; null for every other message, and
	/// for a key that names none, which makes the action malformed: unknown content, as is a target whose sender is not
	/// a JID, since no sender is the account's.
	/// </param>
	/// <param name="pollVote">
	/// A poll vote as the sidecar decrypted it, or as a history poll carries it, without a message; null for every other
	/// message, and for a vote message the sidecar did not decrypt, as a history message's, which is undecryptable.
	/// </param>
	public static MessageContent ToContent(E2E.Message? message, bool viewOnce, MessageTarget? target, PollVote? pollVote) =>
		(message, ToKey(target)) switch
		{
			// REVOKE is the type's zero value (WAWebProtobufsE2E.proto), so only a set type is one.
			({ ReactionMessage: { } reaction }, { } key) => new ReactionContent { Target = key, Emoji = reaction.Text },
			({ ProtocolMessage: { HasType: true, Type: ProtocolType.Revoke } }, { } key) => new RevokeContent { Target = key },
			({ ProtocolMessage: { HasType: true, Type: ProtocolType.MessageEdit, EditedMessage: { } edited } }, { } key) => new EditContent
			{
				Target = key,
				Content = ToContent(edited, viewOnce: false, target: null, pollVote: null),
			},
			({ ProtocolMessage: { HasType: true, Type: ProtocolType.EphemeralSetting } setting }, _) => new DisappearingTimerContent
			{
				Timer = TimeSpan.FromSeconds(setting.EphemeralExpiration),
			},
			({ PollUpdateMessage: not null }, { } key) => ToVote(key, pollVote),
			// A history poll's vote carries no message: history has it decrypted on its poll (ADR 0047).
			(null, { } key) when pollVote is not null => ToVote(key, pollVote),
			({ HasConversation: true }, _) => new TextContent { Text = message.Conversation },
			({ ExtendedTextMessage.HasText: true }, _) => new TextContent { Text = message.ExtendedTextMessage.Text },
			({ ImageMessage: { } image }, _) => ToImage(image, viewOnce),
			({ VideoMessage: { } video }, _) => ToVideo(video, viewOnce),
			({ AudioMessage: { } audio }, _) => ToAudio(audio, viewOnce),
			({ DocumentMessage: { } document }, _) => ToDocument(document),
			({ StickerMessage: { } sticker }, _) => ToSticker(sticker),
			({ LocationMessage: { } location }, _) => new LocationContent
			{
				Latitude = location.DegreesLatitude,
				Longitude = location.DegreesLongitude,
				Name = NullIfEmpty(location.Name),
				Address = NullIfEmpty(location.Address),
			},
			({ ContactMessage: { } contact }, _) => new ContactCardContent { DisplayName = contact.DisplayName, VCard = contact.Vcard },
			_ when PollOf(message) is { PollType: E2E.PollType.Poll } poll => ToPoll(poll),
			_ => new UnknownContent(),
		};

	private static PollVoteContent ToVote(MessageKey poll, PollVote? vote) =>
		new()
		{
			Poll = poll,
			SelectedOptionHashes = vote is null ? [] : [.. vote.SelectedOptionHashes.Select(static hash => ImmutableArray.Create(hash.Span))],
			IsUndecryptable = vote is null or { Undecryptable: true },
		};

	/// <summary>
	/// A poll of any version this client maps, in the sidecar's order (events.pollCreation): V1, V2, V3, V5 and V6 are one
	/// type; V4 is a <c>FutureProofMessage</c>, which mautrix does not map either. Null for every other message.
	/// </summary>
	private static E2E.PollCreationMessage? PollOf(E2E.Message? message) =>
		message?.PollCreationMessage ?? message?.PollCreationMessageV2 ?? message?.PollCreationMessageV3 ?? message?.PollCreationMessageV5 ?? message?.PollCreationMessageV6;

	// A poll that sets no limit (0) lets a voter select every option, as mautrix reads it (pkg/msgconv/wa-poll.go:57-60 at
	// 5dd621f).
	private static PollContent ToPoll(E2E.PollCreationMessage poll) =>
		new()
		{
			Poll = new Poll
			{
				Question = poll.Name,
				Options = [.. poll.Options.Select(static option => option.OptionName)],
				MaxSelectable = poll.SelectableOptionsCount is > 0 and <= int.MaxValue ? (int)poll.SelectableOptionsCount : poll.Options.Count,
			},
		};

	// A view-once message says so in its wrapper, which whatsmeow removed, or in its own flag; mautrix reads both
	// (pkg/connector/events.go:131-132, pkg/msgconv/wa-media.go:52-53).
	private static ImageContent ToImage(E2E.ImageMessage image, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Image, image.URL, image.DirectPath, image.MediaKey, image.FileSHA256, image.FileEncSHA256, image.FileLength, ByteString.Empty),
			Metadata = new ImageMetadata
			{
				MimeType = NullIfEmpty(image.Mimetype),
				Caption = NullIfEmpty(image.Caption),
				Width = image.HasWidth ? ToPixels(image.Width) : null,
				Height = image.HasHeight ? ToPixels(image.Height) : null,
				JpegThumbnail = [.. image.JPEGThumbnail.Span],
			},
			IsViewOnce = viewOnce || image.ViewOnce,
		};

	private static VideoContent ToVideo(E2E.VideoMessage video, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Video, video.URL, video.DirectPath, video.MediaKey, video.FileSHA256, video.FileEncSHA256, video.FileLength, video.StreamingSidecar),
			Metadata = new VideoMetadata
			{
				MimeType = NullIfEmpty(video.Mimetype),
				Caption = NullIfEmpty(video.Caption),
				Width = video.HasWidth ? ToPixels(video.Width) : null,
				Height = video.HasHeight ? ToPixels(video.Height) : null,
				Duration = video.HasSeconds ? TimeSpan.FromSeconds(video.Seconds) : null,
				JpegThumbnail = [.. video.JPEGThumbnail.Span],
			},
			IsViewOnce = viewOnce || video.ViewOnce,
		};

	private static AudioContent ToAudio(E2E.AudioMessage audio, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Audio, audio.URL, audio.DirectPath, audio.MediaKey, audio.FileSHA256, audio.FileEncSHA256, audio.FileLength, audio.StreamingSidecar),
			Metadata = new AudioMetadata
			{
				MimeType = NullIfEmpty(audio.Mimetype),
				Duration = audio.HasSeconds ? TimeSpan.FromSeconds(audio.Seconds) : null,
				IsVoiceNote = audio.PTT,
				Waveform = [.. audio.Waveform.Span],
			},
			IsViewOnce = viewOnce || audio.ViewOnce,
		};

	private static DocumentContent ToDocument(E2E.DocumentMessage document) =>
		new()
		{
			Media = ToRef(
				MediaType.Document,
				document.URL,
				document.DirectPath,
				document.MediaKey,
				document.FileSHA256,
				document.FileEncSHA256,
				document.FileLength,
				ByteString.Empty
			),
			Metadata = new DocumentMetadata
			{
				MimeType = NullIfEmpty(document.Mimetype),
				FileName = NullIfEmpty(document.FileName),
				Caption = NullIfEmpty(document.Caption),
				JpegThumbnail = [.. document.JPEGThumbnail.Span],
			},
		};

	// A sticker is encrypted with the image keys (download.go:95-106).
	private static StickerContent ToSticker(E2E.StickerMessage sticker) =>
		new()
		{
			Media = ToRef(MediaType.Image, sticker.URL, sticker.DirectPath, sticker.MediaKey, sticker.FileSHA256, sticker.FileEncSHA256, sticker.FileLength, ByteString.Empty),
			Metadata = new StickerMetadata
			{
				MimeType = NullIfEmpty(sticker.Mimetype),
				Width = sticker.HasWidth ? ToPixels(sticker.Width) : null,
				Height = sticker.HasHeight ? ToPixels(sticker.Height) : null,
				PngThumbnail = [.. sticker.PngThumbnail.Span],
			},
		};

	private static MediaRef ToRef(
		MediaType type,
		string url,
		string directPath,
		ByteString mediaKey,
		ByteString fileSha256,
		ByteString fileEncSha256,
		ulong fileLength,
		ByteString streamingSidecar
	) =>
		new()
		{
			Type = type,
			DirectPath = directPath,
			Url = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null,
			MediaKey = [.. mediaKey.Span],
			FileSha256 = [.. fileSha256.Span],
			FileEncSha256 = [.. fileEncSha256.Span],
			FileLength = (long)Math.Min(fileLength, long.MaxValue),
			StreamingSidecar = [.. streamingSidecar.Span],
		};

	/// <summary>
	/// The message a reaction, an edit or a revoke names, or a reply quotes; an empty sender is the account's own message.
	/// Null for none, and for a sender that is not a JID: no sender would read as the account's.
	/// </summary>
	private static MessageKey? ToKey(MessageTarget? target) =>
		target switch
		{
			null => null,
			{ Sender.Length: 0 } => new MessageKey { Id = SidecarValues.MessageId(target.Id, "MessageTarget.id") },
			_ when Jid.TryParse(target.Sender, out var sender) => new MessageKey { Id = SidecarValues.MessageId(target.Id, "MessageTarget.id"), Sender = sender },
			_ => null,
		};

	/// <summary>
	/// The message a received one replies to: the quote the sidecar named, with the quoted content from the content's
	/// context; null for no reply, and for a quote that names no sender the client can tell.
	/// </summary>
	/// <param name="message">The message as whatsmeow unwrapped it.</param>
	/// <param name="replyTo">The quote as the sidecar named it, whose empty sender is the account's own message.</param>
	public static QuotedMessage? ToReplyTo(E2E.Message? message, MessageTarget? replyTo) =>
		ToKey(replyTo) is { } quoted
			? new QuotedMessage
			{
				Id = quoted.Id,
				Sender = quoted.Sender,
				Content = ContextOf(message)?.QuotedMessage is { } content ? ToContent(content, viewOnce: false, target: null, pollVote: null) : null,
			}
			: null;

	/// <summary>The users a received message mentions, from its content's context; one that does not parse is left out.</summary>
	public static ImmutableArray<Jid> ToMentions(E2E.Message? message) =>
		ContextOf(message) is { } context ? [.. context.MentionedJID.Select(m => Jid.TryParse(m, out var jid) ? (Jid?)jid : null).OfType<Jid>()] : [];

	/// <summary>
	/// True for a disappearing message: one whatsmeow unwrapped from an ephemeral wrapper, or whose context sets an
	/// expiration, as mautrix reads it (pkg/msgconv/from-whatsapp.go:257-260 at 5dd621f).
	/// </summary>
	public static bool IsEphemeral(E2E.Message? message, bool unwrapped) => unwrapped || ContextOf(message) is { Expiration: > 0 };

	/// <summary>The context of the content kinds this client maps; an edit's is its new content's.</summary>
	private static E2E.ContextInfo? ContextOf(E2E.Message? message) =>
		message switch
		{
			{ ProtocolMessage: { HasType: true, Type: ProtocolType.MessageEdit, EditedMessage: { } edited } } => ContextOf(edited),
			{ ExtendedTextMessage.ContextInfo: { } context } => context,
			{ ImageMessage.ContextInfo: { } context } => context,
			{ VideoMessage.ContextInfo: { } context } => context,
			{ AudioMessage.ContextInfo: { } context } => context,
			{ DocumentMessage.ContextInfo: { } context } => context,
			{ StickerMessage.ContextInfo: { } context } => context,
			{ LocationMessage.ContextInfo: { } context } => context,
			{ ContactMessage.ContextInfo: { } context } => context,
			_ when PollOf(message)?.ContextInfo is { } context => context,
			_ => null,
		};

	/// <summary>Pixels as an <see cref="int"/>, clamped: WhatsApp's are unsigned.</summary>
	private static int ToPixels(uint pixels) => (int)Math.Min(pixels, int.MaxValue);

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
