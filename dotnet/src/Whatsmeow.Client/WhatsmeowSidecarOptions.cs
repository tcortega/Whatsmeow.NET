using Whatsmeow.History;
using Whatsmeow.Messaging;

namespace Whatsmeow;

/// <summary>How <see cref="WhatsmeowSidecar"/> finds and starts the sidecar, or connects to an external one.</summary>
public sealed class WhatsmeowSidecarOptions
{
	/// <summary>
	/// The sidecar binary. When null, the <c>WHATSMEOW_NET_SIDECAR_PATH</c> environment variable is used, and
	/// without it the binary next to the application or under <c>runtimes/&lt;rid&gt;/native/</c>. It must be the
	/// binary itself, not a wrapper script that starts it: on Linux and macOS the sidecar stops when this process is
	/// not its parent.
	/// A single-file app published with <c>IncludeAllContentForSelfExtract</c> must set it to the binary beside the app,
	/// in the directory of <see cref="Environment.ProcessPath"/>, because that mode moves
	/// <see cref="AppContext.BaseDirectory"/>.
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

	/// <summary>
	/// An existing directory the sidecar may read and write media in. A path under it is handed to the sidecar, so no
	/// file bytes cross the socket; the sidecar refuses one that leaves it, by <c>..</c> or a symlink. Any other path,
	/// and every <see cref="Stream"/>, travels in 64 KiB chunks. When null, every transfer uses chunks.
	/// </summary>
	public string? MediaRoot { get; set; }

	/// <summary>
	/// How much history a session asks its phone for when it is paired. Process-wide: they apply to every session paired
	/// while the sidecar runs with them, and never to one paired before (<see cref="HistorySyncOptions"/>).
	/// </summary>
	public HistorySyncOptions History { get; set; } = new();

	/// <summary>
	/// How fast each session's sends leave: a burst, then one every interval, with jitter (<see cref="SendPacingOptions"/>).
	/// Every session uses the same pacing, each with a budget of its own.
	/// </summary>
	public SendPacingOptions SendPacing { get; set; } = new();

	/// <summary>
	/// When set, the client connects to a sidecar it does not start (External mode, ADR 0046), and
	/// <see cref="SidecarPath"/>, <see cref="DatabasePath"/>, <see cref="Transport"/>, <see cref="History"/> and
	/// <see cref="SendPacing"/> must keep their defaults: they configure a sidecar the client starts, and an external one
	/// has its own flags.
	/// <see cref="StartupTimeout"/> bounds the connect, and <see cref="MediaRoot"/> is this process's path to the media
	/// root the sidecar was given with <c>--media-root</c>, which must be the same absolute path on both sides; without it
	/// every transfer uses chunks. When null, the client starts its own sidecar (embedded mode).
	/// </summary>
	public ExternalSidecarOptions? External { get; set; }

	/// <summary>
	/// A copy with copies of <see cref="History"/>, <see cref="SendPacing"/> and <see cref="External"/>, so changing the
	/// caller's instance later changes nothing, a restart included. Member-wise, so a property added later is copied too; a
	/// member-wise copy shares a reference, so a property added later whose type is a mutable class must be deep-copied
	/// here, as these three are.
	/// </summary>
	internal WhatsmeowSidecarOptions Copy()
	{
		var copy = (WhatsmeowSidecarOptions)MemberwiseClone();
		if (History is not null)
			copy.History = History.Copy();
		if (SendPacing is not null)
			copy.SendPacing = SendPacing.Copy();
		if (External is not null)
			copy.External = External.Copy();
		return copy;
	}
}
