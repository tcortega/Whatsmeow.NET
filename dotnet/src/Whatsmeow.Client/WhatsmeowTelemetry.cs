namespace Whatsmeow;

/// <summary>
/// <para>
/// The names to listen to this library's traces and metrics by, for example with OpenTelemetry's
/// <c>AddSource(WhatsmeowTelemetry.ActivitySourceName)</c> and <c>AddMeter(WhatsmeowTelemetry.MeterName)</c>.
/// </para>
/// <para>
/// No span or measurement carries a JID or a phone number. <c>whatsmeow.session.id</c> is the application's own
/// session id; <c>error.type</c> is the <see cref="WhatsmeowErrorCode"/> name of a <see cref="WhatsmeowException"/>,
/// and the full type name of any other exception, never a message.
/// </para>
/// <para>
/// Spans, created only while something listens:
/// <list type="bullet">
/// <item><c>whatsmeow.send</c> (client): one message send; <c>whatsmeow.session.id</c>, <c>whatsmeow.message.id</c>, <c>whatsmeow.message.type</c> (<c>text</c>, <c>image</c>, <c>video</c>, <c>audio</c>, <c>document</c>, <c>sticker</c> or <c>raw</c>), and on failure <c>error.type</c> and the error status.</item>
/// <item><c>whatsmeow.media.upload</c> and <c>whatsmeow.media.download</c> (client): one media transfer; <c>whatsmeow.session.id</c>, <c>whatsmeow.media.type</c> (<c>image</c>, <c>video</c>, <c>audio</c> or <c>document</c>), <c>whatsmeow.media.size</c> (the plaintext's length in bytes, on an upload once it succeeded), and on failure <c>error.type</c> and the error status.</item>
/// </list>
/// </para>
/// <para>
/// Instruments:
/// <list type="bullet">
/// <item><c>whatsmeow.messages.sent</c> (counter, <c>{message}</c>): messages WhatsApp accepted from a send; <c>whatsmeow.session.id</c>.</item>
/// <item><c>whatsmeow.errors</c> (counter, <c>{error}</c>): failed calls to the sidecar, the library's own included, cancellations excluded; <c>error.type</c>.</item>
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
