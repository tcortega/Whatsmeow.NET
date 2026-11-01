namespace Whatsmeow;

/// <summary>How <see cref="WhatsmeowSidecar"/> finds and starts the sidecar.</summary>
public sealed class WhatsmeowSidecarOptions
{
	/// <summary>
	/// The sidecar binary. When null, the <c>WHATSMEOW_NET_SIDECAR_PATH</c> environment variable is used, and
	/// without it the binary next to the application or under <c>runtimes/&lt;rid&gt;/native/</c>.
	/// </summary>
	public string? SidecarPath { get; set; }

	/// <summary>How long the sidecar may take to report ready and answer its first call; more than zero and at most one hour.</summary>
	public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// The SQLite database file that holds every session's keys and state. When null, <c>whatsmeow-net.db</c> in the
	/// current directory. The sidecar creates it readable by the current user only, and only one sidecar may use it
	/// at a time: a second one fails to start. It holds long-term private keys; keep it on an encrypted disk.
	/// </summary>
	public string? DatabasePath { get; set; }

	/// <summary>How the client talks to the sidecar.</summary>
	public SidecarTransport Transport { get; set; }
}
