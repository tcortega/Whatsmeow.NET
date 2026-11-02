namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// A JID that does not parse throws without its text: the message reaches the log of an event the client cannot read
/// (<c>EventUnreadable</c>, at error), and the application's own logs, and the text can hold a phone number
/// (ARCHITECTURE §8.8, ADR 0039). No sidecar: this is the parser's rule.
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
}
