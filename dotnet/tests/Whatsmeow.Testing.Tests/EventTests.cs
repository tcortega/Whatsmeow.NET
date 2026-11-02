using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.History;
using static Whatsmeow.Testing.Tests.Samples;

namespace Whatsmeow.Testing.Tests;

/// <summary>
/// A raised event reaches <c>On</c>, <c>WaitForAsync</c> and <c>OnHandlerError</c> as on a real session (ARCHITECTURE
/// §8.4): per-chat order, chats concurrent, ephemeral lanes apart, handler errors reported. Every handler a test blocks
/// is released by the test, and every wait ends on a condition.
/// </summary>
public sealed class EventTests : IAsyncDisposable
{
	private readonly FakeWhatsAppClient _fake = new(Session);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task OneChatsEvents_RunOneAtATimeInOrder_WhileAnotherChatRuns()
	{
		var ct = TestContext.Current.CancellationToken;
		var release = new TaskCompletionSource();
		var log = new ConcurrentQueue<string>();
		using var handler = _fake.On<MessageReceived>(
			async (e, token) =>
			{
				log.Enqueue($"start {Text(e)}");
				if (Text(e) == "a1")
					await release.Task.WaitAsync(token).ConfigureAwait(false);
				log.Enqueue($"end {Text(e)}");
			}
		);

		var first = _fake.RaiseAsync(Message(Ana, "a1"), ct);
		var second = _fake.RaiseAsync(Message(Ana, "a2"), ct);
		await _fake.RaiseAsync(Message(Bia, "b1"), ct);

		// Bia's chat finished while Ana's first event holds her chat, so Ana's second has not started.
		Assert.DoesNotContain("start a2", log, StringComparer.Ordinal);
		Assert.False(second.IsCompleted);

		release.SetResult();
		await Task.WhenAll(first, second);
		Assert.Equal(["start a1", "end a1", "start a2", "end a2"], log.Where(static line => !line.EndsWith(" b1", StringComparison.Ordinal)), StringComparer.Ordinal);
	}

	[Fact(Timeout = 10_000)]
	public async Task EphemeralEvent_DoesNotWaitForTheChatsBlockedMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		var release = new TaskCompletionSource();
		var typing = new TaskCompletionSource<ChatPresenceChanged>();
		using var messages = _fake.On<MessageReceived>(async (_, token) => await release.Task.WaitAsync(token).ConfigureAwait(false));
		using var presence = _fake.On<ChatPresenceChanged>(
			(e, token) =>
			{
				_ = typing.TrySetResult(e);
				return ValueTask.CompletedTask;
			}
		);

		var message = _fake.RaiseAsync(Message(Ana, "held"), ct);
		var raised = Typing(Ana);
		await _fake.RaiseAsync(raised, ct);

		Assert.Equal(raised, await typing.Task);
		Assert.False(message.IsCompleted);
		release.SetResult();
		await message;
	}

	[Fact(Timeout = 10_000)]
	public async Task HandlerThatThrows_IsReportedOnHandlerError_AndTheNextHandlerAndEventStillRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var boom = new InvalidOperationException("boom");
		var errors = new ConcurrentQueue<HandlerError>();
		var handled = new ConcurrentQueue<string>();
		using var failing = _fake.On<MessageReceived>((e, _) => Text(e) == "first" ? throw boom : ValueTask.CompletedTask);
		using var next = _fake.On<MessageReceived>(
			(e, _) =>
			{
				handled.Enqueue(Text(e));
				return ValueTask.CompletedTask;
			}
		);
		using var onError = _fake.OnHandlerError(errors.Enqueue);

		var first = Message(Ana, "first");
		await _fake.RaiseAsync(first, ct);
		await _fake.RaiseAsync(Message(Ana, "second"), ct);

		Assert.Equal([new HandlerError { Event = first, Exception = boom }], errors);
		Assert.Equal(["first", "second"], handled, StringComparer.Ordinal);
	}

	[Fact(Timeout = 10_000)]
	public async Task EventNoHandlerTakes_IsIgnored()
	{
		var ct = TestContext.Current.CancellationToken;
		var receipts = 0;
		using var other = _fake.On<ReceiptReceived>(
			(_, _) =>
			{
				receipts++;
				return ValueTask.CompletedTask;
			}
		);
		var removed = _fake.On<MessageReceived>((_, _) => throw new InvalidOperationException("a removed handler ran"));
		removed.Dispose();
		var errors = new ConcurrentQueue<HandlerError>();
		using var onError = _fake.OnHandlerError(errors.Enqueue);

		await _fake.RaiseAsync(Message(Ana, "nobody"), ct);

		Assert.Equal(0, receipts);
		Assert.Empty(errors);
	}

	[Fact(Timeout = 10_000)]
	public async Task WaitFor_GetsTheFirstMatchingEventRaisedAfterIt()
	{
		var ct = TestContext.Current.CancellationToken;
		await _fake.RaiseAsync(Message(Ana, "before"), ct);

		var waiting = _fake.WaitForAsync<MessageReceived>(static e => e.Source.Chat == Ana, TimeSpan.FromMinutes(1), ct);
		await _fake.RaiseAsync(Message(Bia, "other chat"), ct);
		var expected = Message(Ana, "after");
		await _fake.RaiseAsync(expected, ct);
		await _fake.RaiseAsync(Message(Ana, "later"), ct);

		Assert.Equal(expected, await waiting);
	}

	[Fact(Timeout = 10_000)]
	public async Task WaitFor_TimesOutOnTheFakesTimeProvider()
	{
		var ct = TestContext.Current.CancellationToken;
		var time = new FakeTimeProvider(Now);
		await using var fake = new FakeWhatsAppClient(Session, time);

		var waiting = fake.WaitForAsync<MessageReceived>(static _ => true, TimeSpan.FromMinutes(1), ct);
		time.Advance(TimeSpan.FromSeconds(59));
		Assert.False(waiting.IsCompleted);
		time.Advance(TimeSpan.FromSeconds(1));

		_ = await Assert.ThrowsAsync<TimeoutException>(() => waiting);
	}

	[Fact(Timeout = 10_000)]
	public async Task WaitForPredicateThatThrows_IsAHandlerError()
	{
		var ct = TestContext.Current.CancellationToken;
		var boom = new InvalidOperationException("boom");
		var errors = new ConcurrentQueue<HandlerError>();
		using var onError = _fake.OnHandlerError(errors.Enqueue);

		var waiting = _fake.WaitForAsync<MessageReceived>(e => Text(e) == "throw" ? throw boom : true, TimeSpan.FromMinutes(1), ct);
		var throwing = Message(Ana, "throw");
		await _fake.RaiseAsync(throwing, ct);
		var matching = Message(Ana, "match");
		await _fake.RaiseAsync(matching, ct);

		Assert.Equal([new HandlerError { Event = throwing, Exception = boom }], errors);
		Assert.Equal(matching, await waiting);
	}

	[Fact(Timeout = 10_000)]
	public async Task Disposed_WaitsEndAndEventsAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var waiting = _fake.WaitForAsync<MessageReceived>(static _ => true, TimeSpan.FromMinutes(1), ct);

		await _fake.DisposeAsync();

		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
		_ = Assert.Throws<ObjectDisposedException>(() => _fake.On<MessageReceived>((_, _) => ValueTask.CompletedTask));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.RaiseAsync(Message(Ana, "late"), ct));
	}

	[Fact(Timeout = 10_000)]
	public async Task Disposed_WhileRaisesWaitBehindABlockedHandler_EveryRaiseEndsObjectDisposed()
	{
		var ct = TestContext.Current.CancellationToken;
		var started = new TaskCompletionSource();
		using var handler = _fake.On<MessageReceived>(
			async (e, token) =>
			{
				_ = started.TrySetResult();
				await new TaskCompletionSource().Task.WaitAsync(token).ConfigureAwait(false);
			}
		);

		// One chat: one event runs, 63 wait in the dispatcher, 64 in its channel, one raise is writing and three wait to write.
		var raises = Enumerable.Range(0, 132).Select(i => _fake.RaiseAsync(Message(Ana, $"m{i}"), ct)).ToArray();
		await started.Task;
		await _fake.DisposeAsync();

		foreach (var raise in raises)
			_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => raise);
	}

	[Fact(Timeout = 10_000)]
	public async Task EventOfItsOwnChat_RaisedFromAHandler_FailsInsteadOfWaitingForever_WhileOtherLanesRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var inner = new TaskCompletionSource<Exception?>();
		var handled = new ConcurrentQueue<string>();
		using var messages = _fake.On<MessageReceived>(
			async (e, token) =>
			{
				handled.Enqueue(Text(e));
				if (Text(e) != "outer")
					return;

				inner.SetResult(await Record.ExceptionAsync(() => _fake.RaiseAsync(Message(Ana, "inner"), token)).ConfigureAwait(false));
				await _fake.RaiseAsync(Message(Bia, "other chat"), token).ConfigureAwait(false);
				await _fake.RaiseAsync(Typing(Ana), token).ConfigureAwait(false);
			}
		);
		using var presence = _fake.On<ChatPresenceChanged>(
			(_, _) =>
			{
				handled.Enqueue("typing");
				return ValueTask.CompletedTask;
			}
		);

		await _fake.RaiseAsync(Message(Ana, "outer"), ct);
		await _fake.RaiseAsync(Message(Ana, "after"), ct);

		Assert.Contains("own lane", Assert.IsType<InvalidOperationException>(await inner.Task).Message, StringComparison.Ordinal);
		Assert.Equal(["outer", "other chat", "typing", "after"], handled, StringComparer.Ordinal);
	}

	[Fact(Timeout = 10_000)]
	public async Task History_RunsInTheHistoryLane_NeverWaitingForItsChatsBlockedMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		var release = new TaskCompletionSource();
		var inner = new TaskCompletionSource<Exception?>();
		var handled = new ConcurrentQueue<string>();
		using var messages = _fake.On<MessageReceived>(async (_, token) => await release.Task.WaitAsync(token).ConfigureAwait(false));
		using var history = _fake.On<HistoryReceived>(
			async (e, token) =>
			{
				handled.Enqueue(((TextContent)e.Messages[0].Content).Text);
				if (inner.Task.IsCompleted)
					return;

				inner.SetResult(await Record.ExceptionAsync(() => _fake.RaiseAsync(Progress(), token)).ConfigureAwait(false));
			}
		);

		var message = _fake.RaiseAsync(Message(Ana, "held"), ct);
		await _fake.RaiseAsync(HistoryBatch(Ana, "old"), ct);
		await _fake.RaiseAsync(HistoryBatch(Bia, "older"), ct);

		// Ana's chat is held, and her history ran: it is in the history lane, which a history handler cannot raise into.
		Assert.False(message.IsCompleted);
		Assert.Equal(["old", "older"], handled, StringComparer.Ordinal);
		Assert.Contains("own lane (\"@history\")", Assert.IsType<InvalidOperationException>(await inner.Task).Message, StringComparison.Ordinal);
		release.SetResult();
		await message;
	}

	[Fact(Timeout = 10_000)]
	public async Task WorkAHandlerStarted_RaisesIntoItsChat_OnceTheHandlerFinished()
	{
		var ct = TestContext.Current.CancellationToken;
		var outerDone = new TaskCompletionSource();
		Task? later = null;
		var handled = new ConcurrentQueue<string>();
		using var messages = _fake.On<MessageReceived>(
			(e, _) =>
			{
				handled.Enqueue(Text(e));
				if (Text(e) == "outer")
				{
					later = Task.Run(
						async () =>
						{
							await outerDone.Task.ConfigureAwait(false);
							await _fake.RaiseAsync(Message(Ana, "later"), ct).ConfigureAwait(false);
						},
						ct
					);
				}

				return ValueTask.CompletedTask;
			}
		);

		await _fake.RaiseAsync(Message(Ana, "outer"), ct);
		outerDone.SetResult();
		await later!;

		Assert.Equal(["outer", "later"], handled, StringComparer.Ordinal);
	}

	[Fact(Timeout = 10_000)]
	public async Task EventOfAnotherSession_IsRefused()
	{
		var e = Message(Ana, "elsewhere", SessionId.From("other"));

		var refused = await Assert.ThrowsAsync<ArgumentException>(() => _fake.RaiseAsync(e, TestContext.Current.CancellationToken));

		Assert.Equal("e", refused.ParamName);
	}
}
