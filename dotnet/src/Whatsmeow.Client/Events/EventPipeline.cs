using System.Collections.Immutable;
using System.Threading.Channels;
using Whatsmeow.Core.Dispatching;

namespace Whatsmeow.Events;

/// <summary>
/// One session's handlers and the dispatchers that run them (ARCHITECTURE §8.4), whatever feeds the events: the
/// <see cref="SessionEvents"/> readers of the sidecar's streams, or <c>FakeWhatsAppClient</c> (ADR 0036). It holds
/// <see cref="IWhatsAppClient.On{TEvent}"/>, <see cref="IWhatsAppClient.WaitForAsync{TEvent}"/> and
/// <see cref="IWhatsAppClient.OnHandlerError"/>, and two dispatchers: the durable one, which acks through
/// <c>ackAsync</c>, and the ephemeral one, which never acks, so ephemeral events never move, hold back or delay the
/// durable ones (ADR 0027).
/// </summary>
internal sealed class EventPipeline : IAsyncDisposable
{
	private readonly SessionId _sessionId;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _log;
	private readonly Channel<Work> _durable = Channel.CreateBounded<Work>(new BoundedChannelOptions(Dispatcher.MaxPending) { SingleReader = true, SingleWriter = true });
	private readonly Dispatcher _durableDispatcher;
	private readonly Channel<Work> _ephemeral = Channel.CreateBounded<Work>(new BoundedChannelOptions(Dispatcher.MaxPending) { SingleReader = true, SingleWriter = true });
	private readonly Dispatcher _ephemeralDispatcher;

	// Never disposed: a handler that deletes its own session still runs, and reads its token, after the stop returned.
#pragma warning disable CA2213 // Justification: see above; it holds no timer and no unmanaged resource
	private readonly CancellationTokenSource _stopping = new();
#pragma warning restore CA2213
	private readonly Lock _lock = new();
	private ImmutableArray<Func<WhatsAppEvent, CancellationToken, ValueTask>> _handlers = [];
	private ImmutableArray<Action<HandlerError>> _errorHandlers = [];
	private Task[] _feeds = [];
	private int _started;
	private int _disposed;

	/// <param name="sessionId">The session, for the logs.</param>
	/// <param name="ackAsync">Acks the durable events up to a seq; null when nothing acks them, as for the fake.</param>
	/// <param name="timeProvider">The clock of the dispatchers and of <see cref="WaitForAsync{TEvent}"/>'s timeout.</param>
	/// <param name="log">The handlers' failures and the dispatchers' logs.</param>
	public EventPipeline(SessionId sessionId, Func<ulong, CancellationToken, Task>? ackAsync, TimeProvider timeProvider, ILogger log)
	{
		_sessionId = sessionId;
		_timeProvider = timeProvider;
		_log = log;
		_durableDispatcher = new Dispatcher(_durable.Reader, ackAsync, timeProvider, log);
		_ephemeralDispatcher = new Dispatcher(_ephemeral.Reader, ackAsync: null, timeProvider, log);
	}

	/// <summary>Stopped: no event comes any more.</summary>
	public bool IsDisposed => _disposed != 0;

	/// <summary>Where the durable events go, in seq order, from one writer.</summary>
	public ChannelWriter<Work> Durable => _durable.Writer;

	/// <summary>Where the ephemeral events go, in arrival order, from one writer.</summary>
	public ChannelWriter<Work> Ephemeral => _ephemeral.Writer;

	/// <summary>
	/// Starts the dispatchers, then each feed with the token of the stop, which waits for the feeds before it stops the
	/// dispatchers. Calling it again does nothing.
	/// </summary>
	public void Start(params ReadOnlySpan<Func<CancellationToken, Task>> feeds)
	{
		if (_disposed != 0 || Interlocked.Exchange(ref _started, 1) != 0)
			return;

		_durableDispatcher.Start();
		_ephemeralDispatcher.Start();
		var started = new Task[feeds.Length];
		for (var i = 0; i < feeds.Length; i++)
			started[i] = feeds[i](_stopping.Token);
		_feeds = started;
	}

	/// <summary>The dispatcher's work for <paramref name="e"/>, in its lane; an event this client cannot map (null) runs no handler.</summary>
	public Work ToWork(ulong seq, WhatsAppEvent? e) =>
		new()
		{
			Seq = seq,
			Lane = EventMapping.Lane(e),
			RunAsync = e is null ? static _ => ValueTask.CompletedTask : ct => RunAsync(e, ct),
		};

	public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent
	{
		ArgumentNullException.ThrowIfNull(handler);
		Func<WhatsAppEvent, CancellationToken, ValueTask> run = (e, ct) => e is TEvent typed ? handler(typed, ct) : ValueTask.CompletedTask;
		lock (_lock)
			_handlers = _handlers.Add(run);
		return new Registration(() =>
		{
			lock (_lock)
				_handlers = _handlers.Remove(run);
		});
	}

	public IDisposable OnHandlerError(Action<HandlerError> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		lock (_lock)
			_errorHandlers = _errorHandlers.Add(handler);
		return new Registration(() =>
		{
			lock (_lock)
				_errorHandlers = _errorHandlers.Remove(handler);
		});
	}

	public async Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
		where TEvent : WhatsAppEvent
	{
		ArgumentNullException.ThrowIfNull(predicate);
		var found = new TaskCompletionSource<TEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

		// A handler like any other, in the same dispatcher, so it sees the event in its lane's order.
		using var registration = On<TEvent>(
			(e, token) =>
			{
				if (predicate(e))
					_ = found.TrySetResult(e);
				return ValueTask.CompletedTask;
			}
		);
		using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
		try
		{
			return await found.Task.WaitAsync(timeout, _timeProvider, stopped.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// No event comes any more: the client was deleted or its sidecar disposed.
			ObjectDisposedException.ThrowIf(condition: true, typeof(IWhatsAppClient));
			throw;
		}
	}

	/// <summary>Cancelled once the stop began: no event runs any more.</summary>
	public CancellationToken Stopping => _stopping.Token;

	/// <summary><see cref="StopAsync"/>, waiting for every handler.</summary>
	public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

	/// <summary>
	/// Stops the feeds, then lets the dispatchers stop their handlers and ack what they finished (ARCHITECTURE §8.4). Once
	/// <paramref name="stopWaiting"/> is cancelled, it no longer waits for handlers that ignore their cancellation (ADR 0034).
	/// </summary>
	public async Task StopAsync(CancellationToken stopWaiting)
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		await _stopping.CancelAsync().ConfigureAwait(false);
		try
		{
			foreach (var feed in _feeds)
				await feed.ConfigureAwait(false);
		}
		finally
		{
			await _durableDispatcher.StopAsync(stopWaiting).ConfigureAwait(false);
			await _ephemeralDispatcher.StopAsync(stopWaiting).ConfigureAwait(false);

			// Already stopped, so these return at once; they keep the dispose visible to the analyzers.
			await _durableDispatcher.DisposeAsync().ConfigureAwait(false);
			await _ephemeralDispatcher.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Runs every handler of <paramref name="e"/> in order. A handler that throws is logged and reported, and the next one runs.</summary>
	private async ValueTask RunAsync(WhatsAppEvent e, CancellationToken cancellationToken)
	{
		ImmutableArray<Func<WhatsAppEvent, CancellationToken, ValueTask>> handlers;
		lock (_lock)
			handlers = _handlers;

		foreach (var handler in handlers)
		{
			try
			{
				await handler(e, cancellationToken).ConfigureAwait(false);
			}
#pragma warning disable CA1031 // Justification: rethrown; whatever a handler throws once the stop began means it did not finish
			catch (Exception) when (cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
			{
				// Stopping: the event is not handled, and comes again.
				throw;
			}
#pragma warning disable CA1031 // Justification: a handler failure never blocks the stream; it is reported on OnHandlerError (ARCHITECTURE §8.4)
			catch (Exception exception)
#pragma warning restore CA1031
			{
				EventLog.HandlerFailed(_log, exception, e.GetType().Name, _sessionId.Value);
				ReportError(new HandlerError { Event = e, Exception = exception });
			}
		}
	}

	private void ReportError(HandlerError error)
	{
		ImmutableArray<Action<HandlerError>> callbacks;
		lock (_lock)
			callbacks = _errorHandlers;

		foreach (var callback in callbacks)
		{
			try
			{
				callback(error);
			}
#pragma warning disable CA1031 // Justification: an error callback that throws must not stop the others or the stream
			catch (Exception exception)
#pragma warning restore CA1031
			{
				EventLog.ErrorCallbackFailed(_log, exception, _sessionId.Value);
			}
		}
	}

	private sealed class Registration(Action remove) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				remove();
		}
	}
}
