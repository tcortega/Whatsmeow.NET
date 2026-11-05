namespace Whatsmeow.Sidecar;

/// <summary>An external sidecar's token, from the file its <c>--token-file</c> reads too (ADR 0046).</summary>
internal static class ExternalToken
{
	private const int HexLength = 64;

	private static readonly System.Buffers.SearchValues<char> s_hex = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

	/// <summary>
	/// Reads the token: one line, 32 bytes as hex, the final line ending optional, as the sidecar reads it. An error
	/// never carries what the file holds.
	/// </summary>
	/// <exception cref="SidecarUnavailableException">The file cannot be read, or it holds no token.</exception>
	public static string Read(string path)
	{
		string content;
		try
		{
			using var reader = new StreamReader(Open(path));
			content = reader.ReadToEnd();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			SidecarUnavailableException.ThrowTokenUnreadable(path, e.Message, e);
			throw;
		}

		var token = content.EndsWith('\n') ? content[..^1] : content;
		token = token.EndsWith('\r') ? token[..^1] : token;
		if (token.Length != HexLength || token.AsSpan().ContainsAnyExcept(s_hex))
			SidecarUnavailableException.ThrowTokenUnreadable(path, "it must hold one line, 32 bytes as hex", innerException: null);

		return token;
	}

	/// <summary>
	/// Opens the file sharing write and delete, so an orchestrator can rename a new token over it while it is read, on
	/// Windows too (docs/guides/external-mode.md).
	/// </summary>
	internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
