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
}
