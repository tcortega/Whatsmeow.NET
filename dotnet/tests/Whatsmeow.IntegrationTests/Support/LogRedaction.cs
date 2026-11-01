using System.Text.RegularExpressions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Redacts phone numbers from a log line the live tests keep: the user part of every JID (a run of seven or more
/// digits before <c>@</c>, <c>.</c> or <c>:</c>, as in <c>15551234567.0:7@s.whatsapp.net</c>), whatever the
/// environment says, and the test number wherever it appears. whatsmeow logs the account's JID at info level
/// ("Successfully paired", pair.go:155).
/// </summary>
internal static partial class LogRedaction
{
	public const string Redacted = "<redacted>";

	public static string Redact(string line, string phoneNumber)
	{
		var redacted = JidUser().Replace(line, Redacted);
		var digits = new string([.. phoneNumber.Where(char.IsAsciiDigit)]);
		if (phoneNumber.Length > 0)
			redacted = redacted.Replace(phoneNumber, Redacted, StringComparison.Ordinal);
		if (digits.Length > 0)
			redacted = redacted.Replace(digits, Redacted, StringComparison.Ordinal);
		return redacted;
	}

	/// <summary>A JID's user part: seven or more digits, so a clock's <c>16:08</c> stays.</summary>
	[GeneratedRegex("[0-9]{7,}(?=[@.:])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
	private static partial Regex JidUser();
}
