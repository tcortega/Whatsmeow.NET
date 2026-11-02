using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.LiveTests;

/// <summary>
/// One live sidecar's log: every record goes to a file in <c>dotnet/artifacts/live/</c> with every JID's user part and
/// the test number redacted (<see cref="LogRedaction"/>), and stays in <see cref="Capture"/> for the tests to wait on.
/// </summary>
internal sealed class LiveLog : IDisposable
{
	private readonly Lock _lock = new();
	private StreamWriter? _writer;

	public LiveLog(string path)
	{
		_writer = new StreamWriter(path, append: false) { AutoFlush = true };
		Capture = new LogCapture(Write);
	}

	public LogCapture Capture { get; }

	/// <summary>Writes a line to the file, redacted.</summary>
	public void Write(string line)
	{
		var redacted = LogRedaction.Redact(line, LiveSettings.PhoneNumber);
		lock (_lock)
			_writer?.WriteLine(redacted);
	}

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
