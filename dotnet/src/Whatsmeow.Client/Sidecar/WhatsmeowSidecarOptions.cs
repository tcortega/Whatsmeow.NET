namespace Whatsmeow.Sidecar;

/// <summary>How <see cref="WhatsmeowSidecar"/> finds and starts the sidecar.</summary>
public sealed class WhatsmeowSidecarOptions
{
	/// <summary>
	/// The sidecar binary. When null, the <c>WHATSMEOW_NET_SIDECAR_PATH</c> environment variable is used, and
	/// without it the binary next to the application or under <c>runtimes/&lt;rid&gt;/native/</c>.
	/// </summary>
	public string? SidecarPath { get; set; }

	/// <summary>How long the sidecar may take to report ready and answer its first call; more than zero.</summary>
	public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>How the client talks to the sidecar.</summary>
	public SidecarTransport Transport { get; set; }
}
