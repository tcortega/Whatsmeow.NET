using System.Diagnostics;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Core.Telemetry;

/// <summary>
/// The library's one <see cref="ActivitySource"/> and its spans (ARCHITECTURE §8.8). Each start returns null, and
/// allocates nothing, while no listener samples the source; tags are set only on a span that exists. No tag carries a
/// JID or a phone number.
/// </summary>
internal static class Tracing
{
	public const string SessionIdTag = "whatsmeow.session.id";
	public const string ErrorTypeTag = "error.type";
	public const string EventTypeTag = "whatsmeow.event.type";
	private const string MessageIdTag = "whatsmeow.message.id";
	private const string MessageTypeTag = "whatsmeow.message.type";
	private const string MediaTypeTag = "whatsmeow.media.type";
	private const string MediaSizeTag = "whatsmeow.media.size";

	public static ActivitySource Source { get; } = new(WhatsmeowTelemetry.ActivitySourceName, ClientVersion.Current);

	/// <summary>A span for one send of a <paramref name="messageType"/> message.</summary>
	public static Activity? StartSend(SessionId sessionId, MessageId messageId, string messageType)
	{
		var activity = Source.StartActivity("whatsmeow.send", ActivityKind.Client);
		if (activity is null)
			return null;

		_ = activity.SetTag(SessionIdTag, sessionId.Value).SetTag(MessageIdTag, messageId.Value).SetTag(MessageTypeTag, messageType);
		return activity;
	}

	/// <summary>
	/// A span for running the handlers of one <paramref name="eventType"/> event, a root: no context crosses the socket,
	/// and the dispatchers run with no context of the application's (<see cref="Events.EventPipeline.Start"/>).
	/// </summary>
	public static Activity? StartProcess(SessionId sessionId, string eventType)
	{
		var activity = Source.StartActivity("whatsmeow.process", ActivityKind.Consumer);
		if (activity is null)
			return null;

		_ = activity.SetTag(SessionIdTag, sessionId.Value).SetTag(EventTypeTag, eventType);
		return activity;
	}

	/// <summary>A span for one upload; its size is set by <see cref="Transferred"/> once it is known.</summary>
	public static Activity? StartUpload(SessionId sessionId, MediaType type) => StartTransfer("whatsmeow.media.upload", sessionId, type);

	/// <summary>A span for one download of <paramref name="media"/>, whose size is known up front.</summary>
	public static Activity? StartDownload(SessionId sessionId, MediaRef media)
	{
		var activity = StartTransfer("whatsmeow.media.download", sessionId, media.Type);
		Transferred(activity, media);
		return activity;
	}

	public static void Transferred(Activity? activity, MediaRef media) => activity?.SetTag(MediaSizeTag, media.FileLength);

	/// <summary>
	/// Marks the span failed with <paramref name="exception"/>'s <c>error.type</c> and the error status, with no
	/// description: the conventions advise against repeating <c>error.type</c> there, and the exception's message, which
	/// they suggest, may hold a JID or a phone number. A cancellation is no failure, and leaves the span as it is.
	/// </summary>
	public static void Failed(Activity? activity, Exception exception)
	{
		if (activity is null || IsCancellation(exception))
			return;

		_ = activity.SetTag(ErrorTypeTag, ErrorType(exception)).SetStatus(ActivityStatusCode.Error);
	}

	/// <summary>
	/// The one rule for every span and instrument (ADR 0037): a cancellation is not an error, so it sets no
	/// <c>error.type</c>, no error status, no error count and no handler duration.
	/// </summary>
	public static bool IsCancellation(Exception exception) => exception is OperationCanceledException;

	/// <summary>The <c>error.type</c> of <paramref name="exception"/>: its error code, or its type's full name.</summary>
	public static string ErrorType(Exception exception) =>
		exception is WhatsmeowException whatsmeow ? Name(whatsmeow.ErrorCode) : exception.GetType().FullName ?? exception.GetType().Name;

	public static string Name(WhatsmeowErrorCode code) => code.ToString();

	private static Activity? StartTransfer(string name, SessionId sessionId, MediaType type)
	{
		var activity = Source.StartActivity(name, ActivityKind.Client);
		if (activity is null)
			return null;

		_ = activity.SetTag(SessionIdTag, sessionId.Value).SetTag(MediaTypeTag, Name(type));
		return activity;
	}

	private static string Name(MediaType type) =>
		type switch
		{
			MediaType.Image => "image",
			MediaType.Video => "video",
			MediaType.Audio => "audio",
			MediaType.Document => "document",
			_ => "unknown",
		};
}
