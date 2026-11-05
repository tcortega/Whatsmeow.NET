using Microsoft.Extensions.Logging.Testing;

namespace Whatsmeow.IntegrationTests.Support;

public sealed partial class LogCaptureTests
{
	[Fact(Timeout = 10_000)]
	public async Task Wait_BufferedAndNewRecords_TestsEachRecordOnce()
	{
		using var logs = new LogCapture();
		var logger = logs.Factory.CreateLogger<LogCaptureTests>();
		for (var i = 0; i < 100; i++)
			Message(logger, "noise");

		var seen = new HashSet<FakeLogRecord>();
		var wait = logs.WaitForAsync(record =>
		{
			Assert.True(seen.Add(record), "The same record was tested twice.");
			return record.Message == "match";
		}, TestContext.Current.CancellationToken);
		Message(logger, "match");

		Assert.Equal("match", (await wait).Message);
		Assert.Equal(101, seen.Count);
	}

	[Fact(Timeout = 10_000)]
	public async Task Wait_CancelledWhileScanning_StopsBeforeTheNextRecord()
	{
		using var logs = new LogCapture();
		var logger = logs.Factory.CreateLogger<LogCaptureTests>();
		for (var i = 0; i < 100; i++)
			Message(logger, "noise");

		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var tested = 0;
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logs.WaitForAsync(_ =>
		{
			tested++;
			cancel.Cancel();
			return false;
		}, cancel.Token));

		Assert.Equal(1, tested);
	}

	[Fact(Timeout = 10_000)]
	public async Task Wait_CancelledWithoutANewRecord_EndsTheWait()
	{
		using var logs = new LogCapture();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var wait = logs.WaitForAsync(_ => false, cancel.Token);
		Assert.False(wait.IsCompleted);

		await cancel.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
	}

	[Fact(Timeout = 10_000)]
	public async Task Wait_TheNextRestartFails_ReturnsItsFailureInsteadOfAnEarlierRestart()
	{
		using var logs = new LogCapture();
		var logger = logs.Factory.CreateLogger<LogCaptureTests>();
		Restarted(logger);
		Failed(logger, new IOException("earlier failure"));
		var wait = logs.WaitForAsync(r => r.Id.Name is "SidecarRestarted" or "SidecarRestartFailed", 3, TestContext.Current.CancellationToken);
		Assert.False(wait.IsCompleted);

		var failure = new IOException("next failure");
		Failed(logger, failure);

		var record = await wait;
		Assert.Equal("SidecarRestartFailed", record.Id.Name);
		Assert.Same(failure, record.Exception);
	}

	[Fact(Timeout = 10_000)]
	public async Task Started_TheNextLaunchHasNotStarted_WaitsForItsOwnProcessAndEndpoint()
	{
		using var logs = new LogCapture();
		var logger = logs.Factory.CreateLogger<LogCaptureTests>();
		Started(logger, 1, "first");
		var wait = logs.StartedAsync(2, TestContext.Current.CancellationToken);
		Assert.False(wait.IsCompleted);

		Message(logger, "unrelated");
		Started(logger, 2, "second");

		Assert.Equal((2, "second"), await wait);
		Assert.Equal((1, "first"), await logs.StartedAsync(1, TestContext.Current.CancellationToken));
	}

	[LoggerMessage(Level = LogLevel.Information, EventName = "SidecarRestarted", Message = "Restarted")]
	private static partial void Restarted(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, EventName = "SidecarRestartFailed", Message = "Restart failed")]
	private static partial void Failed(ILogger logger, Exception exception);

	[LoggerMessage(Level = LogLevel.Information, EventName = "SidecarStarted", Message = "Started {ProcessId} at {Endpoint}")]
	private static partial void Started(ILogger logger, int processId, string endpoint);

	[LoggerMessage(Level = LogLevel.Debug, Message = "{Text}")]
	private static partial void Message(ILogger logger, string text);
}
