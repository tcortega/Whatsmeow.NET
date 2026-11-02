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
			content = File.ReadAllText(path);
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
}
