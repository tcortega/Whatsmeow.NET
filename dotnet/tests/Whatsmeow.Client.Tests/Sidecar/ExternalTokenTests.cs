using System.Security.Cryptography;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Client.Tests.Sidecar;

/// <summary>An external sidecar's token file, as an orchestrator rotates it (docs/guides/external-mode.md, #132).</summary>
public sealed class ExternalTokenTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("wmn-token-").FullName;

	public void Dispose() => Directory.Delete(_directory, recursive: true);

	/// <summary>
	/// A rename over the file lands while the client holds it open on Linux and macOS. On Windows it fails while any
	/// handle is open, so a writer retries until the client's read ends (docs/guides/external-mode.md): the file keeps
	/// the old token whole meanwhile, and the client holds no handle once <see cref="ExternalToken.Read"/> returns.
	/// </summary>
	[Fact]
	public void Read_ANewTokenRenamedOverTheFileWhileTheClientHoldsItOpen_ReadsTheNewToken_OnWindowsOnceTheReadEnds()
	{
		var path = Path.Combine(_directory, "token");
		var old = NewToken();
		File.WriteAllText(path, old);
		var next = NewToken();
		File.WriteAllText(path + ".next", next);

		using (ExternalToken.Open(path))
		{
			if (!OperatingSystem.IsWindows())
				File.Move(path + ".next", path, overwrite: true);
			if (OperatingSystem.IsWindows())
			{
				_ = Assert.Throws<UnauthorizedAccessException>(() => File.Move(path + ".next", path, overwrite: true));
				Assert.Equal(old.TrimEnd('\n'), ExternalToken.Read(path));
			}
		}

		if (OperatingSystem.IsWindows())
			File.Move(path + ".next", path, overwrite: true);

		Assert.Equal(next.TrimEnd('\n'), ExternalToken.Read(path));
	}

	[Fact]
	public void Read_AFileWrittenInPlaceAndReadHalfWritten_IsUnreadable()
	{
		var path = Path.Combine(_directory, "token");
		File.WriteAllText(path, NewToken()[..20]);

		var error = Assert.Throws<SidecarUnavailableException>(() => ExternalToken.Read(path));
		Assert.Equal($"The sidecar's token file {path} cannot be used: it must hold one line, 32 bytes as hex.", error.Message);
	}

	private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "\n";
}
