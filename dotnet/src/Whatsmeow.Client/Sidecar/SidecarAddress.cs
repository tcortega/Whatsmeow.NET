using System.Globalization;

namespace Whatsmeow.Sidecar;

/// <summary>An External-mode address (ADR 0046): a Unix domain socket's path, or a TCP host and port.</summary>
internal sealed record SidecarAddress
{
	private const string UnixPrefix = "unix:";
	private const string TcpPrefix = "tcp:";

	private SidecarAddress(string? path, string? host, int port)
	{
		Path = path;
		Host = host;
		Port = port;
	}

	/// <summary>The socket's absolute path; null for TCP.</summary>
	public string? Path { get; }

	/// <summary>The TCP host, without the brackets of an IPv6 address; null for a socket.</summary>
	public string? Host { get; }

	public int Port { get; }

	/// <summary>Parses <c>unix:&lt;absolute path&gt;</c> or <c>tcp:&lt;host&gt;:&lt;port&gt;</c>; null when it is neither.</summary>
	public static SidecarAddress? Parse(string? address)
	{
		if (address is null)
			return null;

		if (address.StartsWith(UnixPrefix, StringComparison.Ordinal))
		{
			var path = address[UnixPrefix.Length..];
			return System.IO.Path.IsPathFullyQualified(path) ? new SidecarAddress(path, host: null, port: 0) : null;
		}

		if (!address.StartsWith(TcpPrefix, StringComparison.Ordinal))
			return null;

		var hostPort = address[TcpPrefix.Length..];
		var colon = hostPort.LastIndexOf(':', StringComparison.Ordinal);
		if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
			return null;

		// An IPv6 host is bracketed, as in a URI, so its own colons never read as the port's.
		var host = hostPort[..colon];
		var bracketed = host is ['[', .., ']'];
		if (bracketed)
			host = host[1..^1];

		return host.Length == 0 || (!bracketed && host.Contains(':', StringComparison.Ordinal)) ? null : new SidecarAddress(path: null, host, port);
	}

	/// <summary>The address as written in the options, an IPv6 host bracketed.</summary>
	public override string ToString() =>
		Path is { } path ? UnixPrefix + path
		: Host!.Contains(':', StringComparison.Ordinal) ? string.Create(CultureInfo.InvariantCulture, $"{TcpPrefix}[{Host}]:{Port}")
		: string.Create(CultureInfo.InvariantCulture, $"{TcpPrefix}{Host}:{Port}");
}
