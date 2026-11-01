using System.Runtime.CompilerServices;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A test against real WhatsApp. It is explicit, so not run, unless <see cref="LiveSettings.DatabaseVariable"/> names
/// the live database (and, with <see cref="NeedsPhoneNumber"/>, <see cref="LiveSettings.PhoneNumberVariable"/> the
/// test number): without them the run starts nothing and passes with the test not run, so the live tests never run in
/// CI. This file is the only one where <c>build/ci.sh</c> allows <c>Explicit</c> (docs/guidelines/tests.md).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
	/// <summary>Creates the attribute; xUnit passes the caller's file and line.</summary>
	public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
		: base(sourceFilePath, sourceLineNumber)
	{
		Explicit = !LiveSettings.Enabled;
		// A person scans the code or types it in: WhatsApp's QR codes ran out after 160 seconds when measured.
		Timeout = 300_000;
	}

	/// <summary>Whether the test needs the test number too.</summary>
	public bool NeedsPhoneNumber
	{
		get;
		init
		{
			field = value;
			Explicit = !LiveSettings.Enabled || (value && LiveSettings.PhoneNumber.Length == 0);
		}
	}
}
