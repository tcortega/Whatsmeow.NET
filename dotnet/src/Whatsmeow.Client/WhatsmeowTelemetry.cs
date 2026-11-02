namespace Whatsmeow;

/// <summary>
/// <para>
/// The names to listen to this library's traces and metrics by, for example with OpenTelemetry's
/// <c>AddSource(WhatsmeowTelemetry.ActivitySourceName)</c> and <c>AddMeter(WhatsmeowTelemetry.MeterName)</c>.
/// </para>
/// <para>
/// The library puts no JID or phone number in any span or measurement. <c>whatsmeow.session.id</c> is the
/// application's own session id, on every span and every instrument but the error and restart counts: its cardinality
/// is the number of sessions, and a session id that is a phone number puts that number in every one of them. <c>error.type</c> is the <see cref="WhatsmeowErrorCode"/> name of a <see cref="WhatsmeowException"/>, and the
/// full type name of any other exception; a failed span gets the error status with no description, never the message.
/// A cancellation the caller's own token caused is no error: its span keeps the unset status and no
/// <c>error.type</c>, it is not counted in <c>whatsmeow.errors</c>, and a handler the stop cancelled records no
/// duration. Any other cancellation is a failure, by its exception's type: a call cut by a sidecar that died, or a
/// handler's own timeout. <see cref="IWhatsAppClient.WaitForAsync{TEvent}"/> is no handler: it makes no span and
/// records no duration.
/// </para>
/// <para>
/// Spans, created only while something listens:
/// <list type="bullet">
/// <item><c>whatsmeow.send</c> (client): one message send; <c>whatsmeow.session.id</c>, <c>whatsmeow.message.id</c>, <c>whatsmeow.message.type</c> (<c>text</c>, <c>image</c>, <c>video</c>, <c>audio</c>, <c>document</c>, <c>sticker</c> or <c>raw</c>), and on failure <c>error.type</c> and the error status.</item>
/// <item><c>whatsmeow.media.upload</c> and <c>whatsmeow.media.download</c> (client): one media transfer; <c>whatsmeow.session.id</c>, <c>whatsmeow.media.type</c> (<c>image</c>, <c>video</c>, <c>audio</c> or <c>document</c>), <c>whatsmeow.media.size</c> (the plaintext's length in bytes, on an upload once it succeeded), and on failure <c>error.type</c> and the error status.</item>
/// <item><c>whatsmeow.process</c> (consumer): the handlers of one event, each running inside it, so a handler's own spans are its children; started with the first handler that takes the event, so an event no handler takes has none; a root, since no context crosses the socket and the event loops run with none of the application's; <c>whatsmeow.session.id</c>, <c>whatsmeow.event.type</c> (the event record's type name, such as <c>MessageReceived</c>), and when a handler threw, <c>error.type</c> and the error status.</item>
/// </list>
/// </para>
/// <para>
/// Instruments:
/// <list type="bullet">
/// <item><c>whatsmeow.messages.sent</c> (counter, <c>{message}</c>): messages WhatsApp accepted from a send; <c>whatsmeow.session.id</c>.</item>
/// <item><c>whatsmeow.messages.received</c> (counter, <c>{message}</c>): <see cref="Events.MessageReceived"/> events as they arrive, before their handlers; a redelivery counts again; <c>whatsmeow.session.id</c>.</item>
/// <item><c>whatsmeow.handler.duration</c> (histogram, <c>s</c>): how long one handler ran for one event of its type, a handler that threw included; <c>whatsmeow.session.id</c>, <c>whatsmeow.event.type</c>, and <c>error.type</c> when it threw.</item>
/// <item><c>whatsmeow.dispatcher.queue.length</c> (up/down counter, <c>{event}</c>): events a session's dispatchers received and have not finished; up to 64 more may wait in the channel between the sidecar's stream and each dispatcher, which this does not count; <c>whatsmeow.session.id</c>.</item>
/// <item><c>whatsmeow.errors</c> (counter, <c>{error}</c>): failed calls to the sidecar, the library's own included, but not those the caller cancelled; <c>error.type</c>, the error code, or the exception's type for a cancellation the caller did not cause.</item>
/// <item><c>whatsmeow.sidecar.restarts</c> (counter, <c>{restart}</c>): sidecars started again after one exited on its own.</item>
/// </list>
/// </para>
/// </summary>
public static class WhatsmeowTelemetry
{
	/// <summary>The name of the library's one <see cref="System.Diagnostics.ActivitySource"/>.</summary>
	public const string ActivitySourceName = "Whatsmeow.Client";

	/// <summary>
	/// The name of the library's <see cref="System.Diagnostics.Metrics.Meter"/>: one per <see cref="WhatsmeowSidecar"/>,
	/// from the <see cref="System.Diagnostics.Metrics.IMeterFactory"/> it was started with, or its own without one.
	/// </summary>
	public const string MeterName = "Whatsmeow.Client";
}
