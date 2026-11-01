namespace Whatsmeow;

/// <summary>How the client talks to the sidecar process.</summary>
public enum SidecarTransport
{
	/// <summary>A Unix domain socket in a private directory; the default on every OS, Windows included.</summary>
	UnixDomainSocket,

	/// <summary>A named pipe that only the current user can open; a fallback on Windows only.</summary>
	NamedPipe,
}
