using System.Text.Json.Serialization;

namespace Whatsmeow.Sidecar;

/// <summary>The sidecar's one stdout line (ARCHITECTURE §7.8).</summary>
internal sealed record ReadyLine
{
	public string? Event { get; init; }
	public string? Version { get; init; }
	public int Protocol { get; init; }
	public string? Whatsmeow { get; init; }
	public string? Listen { get; init; }
	public string? Nonce { get; init; }
}

/// <summary>The part of a sidecar stderr line (a zerolog JSON object) the client reads.</summary>
internal sealed record SidecarLogLine
{
	public string? Level { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyLine))]
[JsonSerializable(typeof(SidecarLogLine))]
internal sealed partial class SidecarJsonContext : JsonSerializerContext;
