using System.Text.Json;
using Whatsmeow.Core.Errors;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// A JID that does not parse throws without its text: the message reaches the log of an event the client cannot read
/// (<c>EventUnreadable</c>, at error), and the application's own logs, and the text can hold a phone number
/// (ARCHITECTURE §8.8, ADR 0039). A caller's JID is its invalid argument; one the sidecar sent is the library's fault,
/// an internal error that names the field (ARCHITECTURE §8.7). No sidecar: these are the parsers' rules.
/// </summary>
public sealed class JidParseTests
{
	[Fact]
	public void Parse_NotAJid_ThrowsWithoutItsText()
	{
		var error = Assert.Throws<WhatsmeowValidationException>(() => Jid.Parse("5511900000001:x@s.whatsapp.net"));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.DoesNotContain("5511900000001", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SidecarValue_NotAJid_ThrowsInternalNamingTheFieldWithoutItsText()
	{
		var error = Assert.Throws<WhatsmeowException>(() => SidecarValues.Jid("5511900000001:x@s.whatsapp.net", "MessageSource.chat"));

		Assert.Equal(WhatsmeowErrorCode.Internal, error.ErrorCode);
		Assert.Contains("MessageSource.chat", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("5511900000001", error.ToString(), StringComparison.Ordinal);
	}

	/// <summary>
	/// One table of what reads as a JID and what does not, for this parser and the sidecar's <c>server.ReadableJID</c>,
	/// which keeps an event the client cannot read out of the outbox (#122): the two agree on every row, quirks
	/// included (a number may end in NUL characters, as .NET's integer parse allows).
	/// </summary>
	[Fact]
	public void TryParse_AgreesWithTheSidecarsTable()
	{
		using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(SidecarBinaries.FindRoot(), "sidecar", "internal", "server", "testdata", "jids.json")));
		var disagreeing = table
			.RootElement.EnumerateArray()
			.Where(row => Jid.TryParse(row.GetProperty("jid").GetString(), out _) != row.GetProperty("readable").GetBoolean())
			.Select(row => row.GetProperty("jid").GetString()!.Replace("\0", "\\0", StringComparison.Ordinal))
			.ToList();

		Assert.True(table.RootElement.GetArrayLength() > 0);
		Assert.Empty(disagreeing);
	}
}
