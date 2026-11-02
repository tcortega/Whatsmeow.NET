using System.Runtime.CompilerServices;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A test against real WhatsApp. It is explicit, so not run, unless <see cref="LiveSettings.DatabaseVariable"/> names
/// the live database, and each variable the test needs is set too: <see cref="LiveSettings.PhoneNumberVariable"/> with
/// <see cref="NeedsPhoneNumber"/>, <see cref="LiveSettings.PeerDatabaseVariable"/> with <see cref="NeedsPeer"/>, and
/// <see cref="LiveSettings.OwnerVariable"/> with <see cref="NeedsOwner"/>. Without them the run starts nothing and
/// passes with the test not run, so the live tests never run in CI. This file is the only one where
/// <c>build/ci.sh</c> allows <c>Explicit</c> (docs/guidelines/tests.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
	/// <summary>A person scans the code or types it in: WhatsApp's QR codes ran out after 160 seconds when measured.</summary>
	private const int WorkTimeout = 300_000;

	/// <summary>Creates the attribute; xUnit passes the caller's file and line.</summary>
	public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
		: base(sourceFilePath, sourceLineNumber)
	{
		Explicit = !LiveSettings.Enabled;
		Timeout = WorkTimeout;
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
	/// Whether the test needs the owner at the phone: it waits for the owner's ping (<see cref="LiveSidecar.PingAsync"/>),
	/// with a bound of its own on top of the test's work.
	/// </summary>
	public bool NeedsOwner
	{
		get;
		init
		{
			field = value;
			Explicit = IsOff();
			Timeout = WorkTimeout + (value ? (int)LiveSidecar.PingBound.TotalMilliseconds : 0);
		}
	}

	private bool IsOff() =>
		!LiveSettings.Enabled
		|| (NeedsPhoneNumber && LiveSettings.PhoneNumber.Length == 0)
		|| (NeedsPeer && LiveSettings.PeerDatabase.Length == 0)
		|| (NeedsOwner && !LiveSettings.Owner);
}
