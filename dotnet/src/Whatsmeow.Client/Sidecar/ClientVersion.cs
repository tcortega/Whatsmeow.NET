using System.Reflection;

namespace Whatsmeow.Sidecar;

/// <summary>This client's release version, which the embedded sidecar must equal (ARCHITECTURE §13 invariant 12).</summary>
internal static class ClientVersion
{
	/// <summary>The assembly's informational version without its <c>+build</c> metadata. A metadata read, not activation.</summary>
	public static string Current { get; } = Read();

	private static string Read()
	{
		var version = typeof(ClientVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
		var metadata = version.IndexOf('+', StringComparison.Ordinal);
		return metadata < 0 ? version : version[..metadata];
	}
}
