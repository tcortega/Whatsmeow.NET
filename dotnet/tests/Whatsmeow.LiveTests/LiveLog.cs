using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.LiveTests;

/// <summary>
/// One live sidecar's log: every record goes to a file in <c>dotnet/artifacts/live/</c> with every JID's user part and
/// the test number redacted (<see cref="LogRedaction"/>), and stays in <see cref="Capture"/> for the tests to wait on.
/// </summary>
internal sealed class LiveLog : IDisposable
{
	private readonly Lock _lock = new();
	private readonly string _name;
	private StreamWriter? _writer;

	public LiveLog(string path)
	{
		_name = Path.GetFileName(path);
		_writer = new StreamWriter(path, append: false) { AutoFlush = true };
		Capture = new LogCapture(Write);
	}

	public LogCapture Capture { get; }

	/// <summary>
	/// Writes a line to the file, redacted. A sidecar exit the client did not ask for (the supervisor's
	/// <c>SidecarExited</c>) is made loud: a <c>live:</c> line with the exit code and the UTC time follows it, in the file
	/// and the running test's output, so an outside kill cannot hide in a result. A kill test's own kill shows there too,
	/// beside its "sidecar kill at" line.
	/// </summary>
	public void Write(string line)
	{
		var redacted = LogRedaction.Redact(line, LiveSettings.PhoneNumber);
		lock (_lock)
			_writer?.WriteLine(redacted);
		if (!line.Contains(ExitedUnexpectedly, StringComparison.Ordinal))
			return;

		var loud = LiveClock.Stamp($"live: UNEXPECTED SIDECAR EXIT ({_name}): {redacted}");
		lock (_lock)
			_writer?.WriteLine(loud);
		try
		{
			TestContext.Current.TestOutputHelper?.WriteLine(loud);
		}
		catch (InvalidOperationException)
		{
			// No test is running on this thread's context: the file has it.
		}
	}

	/// <summary>The supervisor's <c>SidecarExited</c> message, with the process id and the exit code around it.</summary>
	private const string ExitedUnexpectedly = "exited unexpectedly with code";

	public void Dispose()
	{
		Capture.Dispose();
		lock (_lock)
		{
			_writer?.Dispose();
			_writer = null;
		}
	}
}
