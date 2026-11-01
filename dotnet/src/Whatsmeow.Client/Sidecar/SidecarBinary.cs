using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Whatsmeow.Sidecar;

/// <summary>Finds the sidecar binary and makes it runnable (ARCHITECTURE §8.5).</summary>
internal static class SidecarBinary
{
	public static string FileName { get; } = OperatingSystem.IsWindows() ? "whatsmeow-net.exe" : "whatsmeow-net";

	/// <summary>
	/// An explicit path (the option, else the environment variable) is the only candidate; otherwise the binary next
	/// to the application, then under <c>runtimes/&lt;rid&gt;/native/</c>. Never <c>Assembly.Location</c>.
	/// </summary>
	public static string Locate(WhatsmeowSidecarOptions options)
	{
		string[] candidates = options.SidecarPath is { } path
			? [Path.GetFullPath(path)]
			: [
				Path.Combine(AppContext.BaseDirectory, FileName),
				Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeIdentifier(), "native", FileName),
			];

		foreach (var candidate in candidates)
		{
			if (File.Exists(candidate))
				return candidate;
		}

		SidecarUnavailableException.ThrowNotFound(candidates);
		return null;
	}

	/// <summary>
	/// The RID of the native package. The CGO-free Linux binary runs on musl and glibc alike, so a musl host uses the
	/// glibc RID (ARCHITECTURE §14 question 6).
	/// </summary>
	private static string RuntimeIdentifier()
	{
		var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		var architecture = RuntimeInformation.ProcessArchitecture;
		var arch = architecture is Architecture.X64 ? "x64" : architecture is Architecture.Arm64 ? "arm64" : $"{architecture}";
		return $"{os}-{arch}";
	}

	/// <summary>Sets the execute bits; returns false when the file system refuses (not the owner, read-only).</summary>
	[UnsupportedOSPlatform("windows")]
	public static bool TryMakeExecutable(string path)
	{
		try
		{
			File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Copies the binary to the user's local application data, where it can be made executable, for a binary on a
	/// <c>noexec</c> mount or one that cannot be chmodded (never to <c>/tmp</c>, which may be <c>noexec</c> too).
	/// The copy is renamed into place, which is safe while another process runs the previous copy.
	/// </summary>
	[UnsupportedOSPlatform("windows")]
	public static string CopyToCache(string path)
	{
		var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "whatsmeow-net", ClientVersion.Current);
		Directory.CreateDirectory(directory);

		var copy = Path.Combine(directory, FileName);
		var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
		File.Copy(path, temporary);
		File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		File.Move(temporary, copy, overwrite: true);
		return copy;
	}
}
