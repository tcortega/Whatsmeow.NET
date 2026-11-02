namespace Whatsmeow;

/// <summary>
/// A sidecar the client connects to but does not start, stop or restart: one an orchestrator runs as its own container
/// or process, with <c>whatsmeow-net --database &lt;file&gt; --listen &lt;address&gt; --token-file &lt;file&gt;</c>
/// (External mode, ADR 0046). Disposing the client leaves it running; when it goes away, the client connects again
/// with a backoff, connects the sessions it had connected, and resumes their events from where it was.
/// </summary>
public sealed class ExternalSidecarOptions
{
	/// <summary>
	/// The address the sidecar listens on, as given to its <c>--listen</c>: <c>unix:&lt;absolute path&gt;</c>, a Unix
	/// domain socket, for example on a volume both containers share, or <c>tcp:&lt;host&gt;:&lt;port&gt;</c>. TCP carries
	/// the token in clear text: use it only on a private network, such as the one between the containers of a pod.
	/// </summary>
	public string? Address { get; set; }

	/// <summary>
	/// The file that holds the sidecar's token, the same file (or secret) its <c>--token-file</c> reads: 32 random bytes
	/// as hex, for example from <c>openssl rand -hex 32</c>. It is read again on every connect, so a sidecar restarted
	/// with a new token is reached with it. Never pass the token in an argument or an environment variable.
	/// </summary>
	public string? TokenPath { get; set; }

	/// <summary>
	/// The name the sidecar keeps this client's event cursors under: 1 to 64 of <c>[A-Za-z0-9._-]</c>, starting with a
	/// letter or a digit. Each client of one sidecar needs its own; two clients with the same name take each other's
	/// event streams. A name no client uses any more keeps its sessions' events in the sidecar's database.
	/// </summary>
	public string Consumer { get; set; } = DefaultConsumer;

	/// <summary>The consumer name of embedded mode, and the default here (ADR 0022).</summary>
	internal const string DefaultConsumer = "client";

	/// <summary>
	/// How long the connection may stay silent before the client pings it, and how long it waits for the answer before it
	/// takes the sidecar for gone: a TCP peer that vanished never closes its end. Internal, for the tests.
	/// </summary>
	internal TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(15);

	internal ExternalSidecarOptions Copy() => (ExternalSidecarOptions)MemberwiseClone();
}
