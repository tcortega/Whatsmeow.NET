namespace Whatsmeow.Sidecar;

/// <summary>The running sidecar's identity.</summary>
public sealed record SidecarInfo
{
	/// <summary>The release version; equal to the client version.</summary>
	public required string Version { get; init; }

	/// <summary>The contract revision the sidecar speaks.</summary>
	public required int Protocol { get; init; }

	/// <summary>The whatsmeow module version the sidecar is built with.</summary>
	public required string WhatsmeowVersion { get; init; }

	/// <summary>The WhatsApp Web version the sidecar announces.</summary>
	public required string WhatsAppWebVersion { get; init; }

	/// <summary>The Go toolchain the sidecar was built with.</summary>
	public required string GoVersion { get; init; }

	/// <summary>The git commit the sidecar was built from.</summary>
	public required string Commit { get; init; }
}
