using System.Runtime.CompilerServices;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A test against real WhatsApp. It is explicit, so not run, unless <see cref="LiveSettings.DatabaseVariable"/> names
/// the live database, and each variable the test needs is set too: <see cref="LiveSettings.PhoneNumberVariable"/> with
/// <see cref="NeedsPhoneNumber"/>, <see cref="LiveSettings.PeerDatabaseVariable"/> with <see cref="NeedsPeer"/>, and
/// <see cref="LiveSettings.OwnerVariable"/> with <see cref="NeedsOwner"/>, <see cref="LiveSettings.MediaSizesVariable"/>
/// with <see cref="NeedsMediaSizes"/>, <see cref="LiveSettings.PushNameFirstSightVariable"/> with
/// <see cref="NeedsPushNameFirstSight"/>, and <see cref="LiveSettings.ContactsFullCycleVariable"/> with
/// <see cref="NeedsContactsFullCycle"/>. Without them the run starts nothing and
/// passes with the test not run, so the live tests never run in CI. This file is the only one where
/// <c>build/ci.sh</c> allows <c>Explicit</c> (docs/guidelines/tests.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
	/// <summary>A person scans the code or types it in: WhatsApp's QR codes ran out after 160 seconds when measured.</summary>
	private const int WorkTimeout = 300_000;

	/// <summary>
	/// What every live test's timeout adds for the session's backlog, which the first test to connect a session waits
	/// for (<see cref="LiveSidecar.CatchUpBound"/>); a test that sets its own timeout adds it too.
	/// </summary>
	public const int CatchUpTimeout = 600_000;

	/// <summary>Creates the attribute; xUnit passes the caller's file and line.</summary>
	public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
		: base(sourceFilePath, sourceLineNumber)
	{
		Explicit = !LiveSettings.Enabled;
		Timeout = WorkTimeout + CatchUpTimeout;
	}

	/// <summary>Whether the test needs the test number too.</summary>
	public bool NeedsPhoneNumber
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
		}
	}

	/// <summary>Whether the test needs the peer session, on a second account (<see cref="LivePeer"/>).</summary>
	public bool NeedsPeer
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
		}
	}

	/// <summary>
	/// Whether the test needs the owner at a phone: it waits for the owner's ping (<see cref="LiveSidecar.PingAsync"/>) or
	/// answer (<see cref="LiveSidecar.AskOwnerAsync"/>), with a bound of its own on top of the test's work.
	/// </summary>
	public bool NeedsOwner
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
			Timeout = WorkTimeout + CatchUpTimeout + (value ? (int)LiveSidecar.PingBound.TotalMilliseconds : 0);
		}
	}

	/// <summary>Whether the test is the media size ladder, which runs only when asked for (<see cref="LiveSettings.MediaSizesVariable"/>).</summary>
	public bool NeedsMediaSizes
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
		}
	}

	/// <summary>
	/// Whether the test is the push name stimulus on the persistent sessions, which spends the peer store's first sight of
	/// the business's push name, so it runs only when asked for (<see cref="LiveSettings.PushNameFirstSightVariable"/>, ADR 0061).
	/// Its timeout is the work, two connects each with its catch-up (the live session's new connection, then the peer's
	/// first), the ladder's bounded waits (<see cref="PushNameStimulus.Bound"/>) and its two restores: 300 s + 2 x (120 s +
	/// 600 s) + 3 x (75 s + 60 s) + 60 s + 2 x 30 s = 2,265 s.
	/// </summary>
	public bool NeedsPushNameFirstSight
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
			if (value)
				Timeout =
					WorkTimeout
					+ (2 * ((int)LiveSidecar.ConnectBound.TotalMilliseconds + CatchUpTimeout))
					+ (int)PushNameStimulus.Bound(LiveChecks.Watch).TotalMilliseconds
					+ (2 * (int)LiveRestore.Bound.TotalMilliseconds);
		}
	}

	/// <summary>
	/// Whether the test is the Contacts FullCycle on the persistent sessions, which spends the peer store's first sight of
	/// any row of the business with no business name, so it runs only when asked for
	/// (<see cref="LiveSettings.ContactsFullCycleVariable"/>, ADR 0062). Its timeout is the work, two connects each with
	/// its catch-up (the live session's, then the peer's), the branches' bounded waits (<see cref="ContactsCycle.Bound"/>),
	/// the picture's two waits, and two restores (the photo, the live session's presence): 300 s + 2 x (120 s + 600 s) +
	/// (60 s + 75 s + 60 s) + 2 x 60 s + 2 x 30 s = 2,115 s.
	/// </summary>
	public bool NeedsContactsFullCycle
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
			if (value)
				Timeout =
					WorkTimeout
					+ (2 * ((int)LiveSidecar.ConnectBound.TotalMilliseconds + CatchUpTimeout))
					+ (int)ContactsCycle.Bound(LiveChecks.Watch).TotalMilliseconds
					+ (2 * (int)LiveChecks.Watch.TotalMilliseconds)
					+ (2 * (int)LiveRestore.Bound.TotalMilliseconds);
		}
	}

	/// <summary>
	/// Whether the test pairs a fresh companion of the peer's account, which needs a QR scan every run: only with
	/// <see cref="LiveSettings.FreshDatabaseVariable"/>, <see cref="LiveSettings.HistoryBoundsVariable"/> and
	/// <see cref="LiveSettings.KnownDevicesVariable"/> set (ADR 0060); it then fails before it starts anything without a
	/// valid <see cref="LiveSettings.HistoryDaysLimitVariable"/>. Its timeout is the scan, the live and the peer
	/// sessions' first catch-up before it, the capture's bound from Paired, which holds the fresh session's catch-up, its
	/// steps and the window, and the cleanup's own bound.
	/// </summary>
	public bool NeedsFreshPairing
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
			if (value)
				Timeout = WorkTimeout + (2 * CatchUpTimeout) + (int)LiveHistoryBounds.Bound.TotalMilliseconds + (int)FreshCompanion.CleanupBound.TotalMilliseconds;
		}
	}

	private bool IsOff() =>
		!LiveSettings.Enabled
		|| (NeedsPhoneNumber && LiveSettings.PhoneNumber.Length == 0)
		|| (NeedsPeer && LiveSettings.PeerDatabase.Length == 0)
		|| (NeedsOwner && !LiveSettings.Owner)
		|| (NeedsMediaSizes && !LiveSettings.MediaSizes)
		|| (NeedsPushNameFirstSight && !LiveSettings.PushNameFirstSight)
		|| (NeedsContactsFullCycle && !LiveSettings.ContactsFullCycle)
		|| (NeedsFreshPairing && (LiveSettings.FreshDatabase.Length == 0 || LiveSettings.HistoryBounds.Length == 0 || !LiveSettings.HasKnownDevices));
}
