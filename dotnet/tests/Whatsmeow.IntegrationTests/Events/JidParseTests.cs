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

	/// <summary>
	/// Each part lands in its own field, and the text comes back in whatsmeow's canonical form, which parses to the same
	/// JID. The canonical forms are what whatsmeow's <c>types.JID.String</c> writes for the same text at the pin: an agent
	/// always with its device, a device without its leading zeros.
	/// </summary>
	[Theory]
	[InlineData("5511900000001@s.whatsapp.net", "5511900000001", 0, 0, "s.whatsapp.net", "5511900000001@s.whatsapp.net")]
	[InlineData("111:2@lid", "111", 0, 2, "lid", "111:2@lid")]
	[InlineData("5511900000001.1:2@s.whatsapp.net", "5511900000001", 1, 2, "s.whatsapp.net", "5511900000001.1:2@s.whatsapp.net")]
	[InlineData("5511900000001.1@s.whatsapp.net", "5511900000001", 1, 0, "s.whatsapp.net", "5511900000001.1:0@s.whatsapp.net")]
	[InlineData("5511900000001:002@s.whatsapp.net", "5511900000001", 0, 2, "s.whatsapp.net", "5511900000001:2@s.whatsapp.net")]
	[InlineData("120363000000000000@g.us", "120363000000000000", 0, 0, "g.us", "120363000000000000@g.us")]
	[InlineData("status@broadcast", "status", 0, 0, "broadcast", "status@broadcast")]
	public void Parse_EachPartInItsField_AndTheCanonicalFormRoundTrips(string text, string user, byte agent, ushort device, string server, string canonical)
	{
		var jid = Jid.Parse(text);

		Assert.Equal((user, agent, device, server), (jid.User, jid.Agent, jid.Device, jid.Server));
		Assert.Equal(canonical, jid.ToString());
		var again = Jid.Parse(jid.ToString());
		Assert.Equal(jid, again);
		Assert.Equal(jid.GetHashCode(), again.GetHashCode());
	}
}
