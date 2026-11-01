using System.Text.RegularExpressions;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Pairing;

/// <summary>
/// What a finished pairing must look like. The fake-mode tests and <c>Whatsmeow.LiveTests</c> check a run with these
/// same assertions (docs/guidelines/tests.md Live WhatsApp); the codes come from WhatsApp, so they are checked by format.
/// </summary>
internal static partial class PairingChecks
{
	/// <summary>
	/// One or more distinct QR codes in whatsmeow's format, then <see cref="PairingUpdate.Paired"/>, and the session
	/// usable right after.
	/// </summary>
	public static void ValidateQrPairing(IReadOnlyList<PairingUpdate> updates, SessionState after)
	{
		Assert.Equal(new PairingUpdate.Paired(), updates[^1]);
		var codes = updates.SkipLast(1).Select(update => Assert.IsType<PairingUpdate.QrCode>(update).Code).ToList();
		Assert.NotEmpty(codes);
		Assert.All(codes, code => Assert.Matches(QrCodeFormat(), code));
		Assert.Distinct(codes, StringComparer.Ordinal);
		AssertUsable(after);
	}

	/// <summary>One pairing code in WhatsApp's format, then <see cref="PairingUpdate.Paired"/>, and the session usable right after.</summary>
	public static void ValidatePhonePairing(IReadOnlyList<PairingUpdate> updates, SessionState after)
	{
		Assert.Collection(
			updates,
			update => Assert.Matches(PairingCodeFormat(), Assert.IsType<PairingUpdate.PairingCode>(update).Code),
			update => Assert.Equal(new PairingUpdate.Paired(), update)
		);
		AssertUsable(after);
	}

	/// <summary>
	/// Connected when <see cref="PairingUpdate.Paired"/> came. WhatsApp may already have sent the offline backlog by
	/// the time the state is read, which makes it CaughtUp [inferred: live only, the fake never sends it].
	/// </summary>
	private static void AssertUsable(SessionState after) =>
		Assert.Contains(after, new SessionState[] { new SessionState.Connected(), new SessionState.CaughtUp() });

	/// <summary>whatsmeow's QR payload: ref, the noise key, the identity key, the ADV secret, the client type (pair.go:125-130).</summary>
	[GeneratedRegex("^https://wa\\.me/settings/linked_devices#.+,[A-Za-z0-9+/]{43}=,[A-Za-z0-9+/]{43}=,[A-Za-z0-9+/]{43}=,[0-9a-z]$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
	private static partial Regex QrCodeFormat();

	/// <summary>Eight characters of WhatsApp's linking alphabet, four and four (pair-code.go:50, 142).</summary>
	[GeneratedRegex("^[1-9A-HJ-NP-TV-Z]{4}-[1-9A-HJ-NP-TV-Z]{4}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
	private static partial Regex PairingCodeFormat();
}
