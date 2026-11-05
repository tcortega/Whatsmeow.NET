using System.Security.Cryptography;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Client.Tests.Sidecar;

/// <summary>An external sidecar's token file, as an orchestrator rotates it (docs/guides/external-mode.md, #132).</summary>
public sealed class ExternalTokenTests : IDisposable
{
	private readonly string _directory = Directory.CreateTempSubdirectory("wmn-token-").FullName;

	public void Dispose() => Directory.Delete(_directory, recursive: true);

	[Fact]
	public void Read_ANewTokenRenamedOverTheFileWhileTheClientHoldsItOpen_ReadsTheNewToken()
	{
		var path = Path.Combine(_directory, "token");
		File.WriteAllText(path, NewToken());
		var next = NewToken();

		using (ExternalToken.Open(path))
		{
			File.WriteAllText(path + ".next", next);
			File.Move(path + ".next", path, overwrite: true);
		}

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
