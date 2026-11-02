using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Core.Dispatching;

namespace Whatsmeow.Client.Tests.Dispatching;

/// <summary>
/// The dispatcher's depth, the queue length metric (ADR 0037): what it holds, events that will never run included,
/// leave it at the stop. Every wait ends on a condition.
/// </summary>
public sealed class DispatcherDepthTests
{
	[Fact(Timeout = 10_000)]
	public async Task Stop_WithEventsUnrun_ReportsThemGone()
	{
		var ct = TestContext.Current.CancellationToken;
		var input = Channel.CreateUnbounded<Work>();
		var changes = Channel.CreateUnbounded<int>();
		var depth = 0;
		var dispatcher = new Dispatcher(input.Reader, ackAsync: null, new FakeTimeProvider(), NullLogger.Instance, change => _ = changes.Writer.TryWrite(change));
		dispatcher.Start();

		// One event holds the lane until the stop cancels it; the next waits behind it and never runs.
		var held = new TaskCompletionSource();
		_ = input.Writer.TryWrite(new Work { Seq = 1, Lane = "a", RunAsync = async token => await held.Task.WaitAsync(token).ConfigureAwait(false) });
		_ = input.Writer.TryWrite(new Work { Seq = 2, Lane = "a", RunAsync = static _ => ValueTask.CompletedTask });
		while (depth != 2)
			depth += await changes.Reader.ReadAsync(ct);

		await dispatcher.DisposeAsync();

		changes.Writer.Complete();
		await foreach (var change in changes.Reader.ReadAllAsync(ct))
			depth += change;
		Assert.Equal(0, depth);
	}
}
