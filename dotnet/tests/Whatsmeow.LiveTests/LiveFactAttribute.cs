using System.Runtime.CompilerServices;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A test against real WhatsApp. It is explicit, so not run, unless <see cref="LiveSettings.PhoneNumberVariable"/>
/// names the dedicated test number: without it the run starts nothing and passes with the test not run, so the live
/// tests never run in CI (docs/guidelines/tests.md Live WhatsApp; build/ci.sh allows this attribute and
/// <c>Explicit</c> only in this project).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveFactAttribute : FactAttribute
{
	/// <summary>Creates the attribute; xUnit passes the caller's file and line.</summary>
	public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
		: base(sourceFilePath, sourceLineNumber)
	{
		Explicit = !LiveSettings.Enabled;
		// A person scans the code or types it in: WhatsApp's codes last about 160 seconds (pair-code.go:83-85).
		Timeout = 300_000;
	}
}
