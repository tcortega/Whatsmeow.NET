using System.Collections.Immutable;
using Whatsmeow.IntegrationTests.Support;

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

	/// <summary>
	/// Set, to any value, to run the media size ladder, 181 MiB of documents to the peer (<c>LiveMediaTests</c>): too
	/// much to send on every run with the peer.
	/// </summary>
	public const string MediaSizesVariable = "WHATSMEOW_LIVE_MEDIA_SIZES";

	/// <summary>
	/// The file of media refs an earlier run uploaded, kept outside the repository, which the ref lifetime test reads and
	/// appends to (<c>LiveMediaTests</c>). Optional: without it that test tries this run's ref only. It holds media keys,
	/// so it is created 0600.
	/// </summary>
	public const string MediaRefsVariable = "WHATSMEOW_LIVE_MEDIA_REFS";

	/// <summary>
	/// Set, to any value, to run the push name stimulus on the persistent sessions (<c>LiveContactsTests</c>, ADR 0061): it
	/// spends the peer store's one first sight of the business's push name, so an ordinary run never sends it.
	/// </summary>
	public const string PushNameFirstSightVariable = "WHATSMEOW_LIVE_PUSHNAME_FIRST_SIGHT";

	/// <summary>
	/// A new database file for the fresh companion of the peer's account (<see cref="LiveFresh"/>, ADR 0060): the opt-in
	/// for the one test that asks for a QR scan every run. The test creates the file and refuses one that exists.
	/// </summary>
	public const string FreshDatabaseVariable = "WHATSMEOW_LIVE_FRESH_DATABASE";

	/// <summary>A new file for the fresh pairing's history bounds record (<see cref="LiveHistoryBounds"/>, #42); refused if it exists.</summary>
	public const string HistoryBoundsVariable = "WHATSMEOW_LIVE_HISTORY_BOUNDS";

	/// <summary>
	/// The peer account's devices known from other stores, device JIDs separated by commas, which the fresh companion must
	/// not be: root's preflight reads them from a consistent snapshot of those stores, without opening them in a sidecar
	/// (ADR 0060). A secret: never printed.
	/// </summary>
	public const string KnownDevicesVariable = "WHATSMEOW_LIVE_KNOWN_DEVICES";

	/// <summary>The live database; empty when the live tests are off.</summary>
	public static string Database { get; } = Environment.GetEnvironmentVariable(DatabaseVariable) ?? "";

	/// <summary>The dedicated number; empty when it is not set.</summary>
	public static string PhoneNumber { get; } = Environment.GetEnvironmentVariable(PhoneNumberVariable) ?? "";

	/// <summary>The peer database; empty when it is not set.</summary>
	public static string PeerDatabase { get; } = Environment.GetEnvironmentVariable(PeerDatabaseVariable) ?? "";

	/// <summary>The fresh companion's new database; empty when it is not set.</summary>
	public static string FreshDatabase { get; } = Environment.GetEnvironmentVariable(FreshDatabaseVariable) ?? "";

	/// <summary>The history bounds record's new file; empty when it is not set.</summary>
	public static string HistoryBounds { get; } = Environment.GetEnvironmentVariable(HistoryBoundsVariable) ?? "";

	/// <summary>Whether <see cref="KnownDevicesVariable"/> is set.</summary>
	public static bool HasKnownDevices { get; } = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KnownDevicesVariable));

	/// <summary>
	/// The devices <see cref="KnownDevicesVariable"/> names, parsed when asked for, so a malformed value fails only the
	/// test that needs it, without its text (<see cref="Jid.Parse(string)"/>, ADR 0039). The fresh companion checks the rest.
	/// </summary>
	public static ImmutableArray<Jid> KnownDevices =>
		[.. (Environment.GetEnvironmentVariable(KnownDevicesVariable) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Jid.Parse)];

	/// <summary>The media refs file; empty when it is not set.</summary>
	public static string MediaRefs { get; } = Environment.GetEnvironmentVariable(MediaRefsVariable) ?? "";

	/// <summary>Whether the media size ladder runs.</summary>
	public static bool MediaSizes { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(MediaSizesVariable));

	/// <summary>Whether the push name stimulus runs.</summary>
	public static bool PushNameFirstSight { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PushNameFirstSightVariable));

	/// <summary>Whether the owner is at the phone.</summary>
	public static bool Owner { get; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OwnerVariable));

	/// <summary>
	/// Where each run backs up the live and peer databases before their first start, <c>~/.whatsmeow-net-live/backup/</c>
	/// (0700, the files 0600): the one sanctioned copy of them.
	/// </summary>
	public static string BackupDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".whatsmeow-net-live", "backup");

	/// <summary>Whether the live tests run.</summary>
	public static bool Enabled => Database.Length > 0;
}
