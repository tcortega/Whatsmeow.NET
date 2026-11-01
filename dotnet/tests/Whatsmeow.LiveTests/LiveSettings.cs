namespace Whatsmeow.LiveTests;

/// <summary>The live tests' opt-in: the dedicated test number, from the environment. It is a secret; never print it.</summary>
internal static class LiveSettings
{
	/// <summary>The variable that turns the live tests on, with the dedicated number in international format.</summary>
	public const string PhoneNumberVariable = "WHATSMEOW_LIVE_PHONE_NUMBER";

	/// <summary>The dedicated number; empty when the live tests are off.</summary>
	public static string PhoneNumber { get; } = Environment.GetEnvironmentVariable(PhoneNumberVariable) ?? "";

	/// <summary>Whether the live tests run.</summary>
	public static bool Enabled => PhoneNumber.Length > 0;
}
