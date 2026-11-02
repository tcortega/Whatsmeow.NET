namespace Whatsmeow.LiveTests;

/// <summary>
/// The live tests' opt-in, from the environment (docs/guidelines/tests.md Live WhatsApp). The number and the database
/// paths are secrets: never print them.
/// </summary>
internal static class LiveSettings
{
	/// <summary>
	/// The variable that turns the live tests on: the persistent database of the live session, which holds the
	/// account's keys.
	/// </summary>
	public const string DatabaseVariable = "WHATSMEOW_LIVE_DATABASE";

	/// <summary>The dedicated test number in international format, which only the phone-code test needs.</summary>
	public const string PhoneNumberVariable = "WHATSMEOW_LIVE_PHONE_NUMBER";

	/// <summary>
	/// The persistent database of the peer session (<see cref="LivePeer"/>), on a second account: the tests that send
	/// between two accounts need it.
	/// </summary>
	public const string PeerDatabaseVariable = "WHATSMEOW_LIVE_PEER_DATABASE";

	/// <summary>Set, to any value, when the owner is at the phone for the run: the tests that need the phone itself need it.</summary>
	public const string OwnerVariable = "WHATSMEOW_LIVE_OWNER";

	/// <summary>The live database; empty when the live tests are off.</summary>
	public static string Database { get; } = Environment.GetEnvironmentVariable(DatabaseVariable) ?? "";

	/// <summary>The dedicated number; empty when it is not set.</summary>
	public static string PhoneNumber { get; } = Environment.GetEnvironmentVariable(PhoneNumberVariable) ?? "";

	/// <summary>The peer database; empty when it is not set.</summary>
	public static string PeerDatabase { get; } = Environment.GetEnvironmentVariable(PeerDatabaseVariable) ?? "";

	/// <summary>Whether the owner is at the phone.</summary>
	public static bool Owner { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OwnerVariable));

	/// <summary>Whether the live tests run.</summary>
	public static bool Enabled => Database.Length > 0;
}
