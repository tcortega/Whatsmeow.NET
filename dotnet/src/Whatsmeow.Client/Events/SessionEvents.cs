using System.Collections.Immutable;
using System.Threading.Channels;
using Grpc.Core;
using Whatsmeow.Core.Dispatching;
using Whatsmeow.Core.Errors;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Events;

/// <summary>
/// One session's events in this process (ARCHITECTURE §8.4): the reader of <c>StreamEvents</c>, which only decodes
/// and writes to a bounded channel, the <see cref="Dispatcher"/> behind it, and the handlers it runs. The ephemeral
/// events (presence, chat presence) come on <c>StreamEphemeralEvents</c>, with a reader, channel and dispatcher of
/// their own that never ack, so they never move, hold back or delay the durable ones (ADR 0027). It starts on the
/// first successful connect of the session in this process.
/// </summary>
internal sealed class SessionEvents : IAsyncDisposable
{
	/// <summary>The one consumer of a session's events in embedded mode: one client process per sidecar (ADR 0022).</summary>
	public const string Consumer = "client";

	/// <summary>How long the reader waits before it opens a stream again after a failure that is not the sidecar going away.</summary>
	private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

	private readonly WhatsmeowSidecar _sidecar;
	private readonly EventService.EventServiceClient _client;
	private readonly SessionId _sessionId;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _log;
	private readonly Channel<Work> _channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(Dispatcher.MaxPending) { SingleReader = true, SingleWriter = true });
	private readonly Dispatcher _dispatcher;
	private readonly Channel<Work> _ephemeralChannel = Channel.CreateBounded<Work>(new BoundedChannelOptions(Dispatcher.MaxPending) { SingleReader = true, SingleWriter = true });
	private readonly Dispatcher _ephemeralDispatcher;

	// Never disposed: a handler that deletes its own session still runs, and reads its token, after the stop returned.
#pragma warning disable CA2213 // Justification: see above; it holds no timer and no unmanaged resource
	private readonly CancellationTokenSource _stopping = new();
#pragma warning restore CA2213
	private readonly Lock _lock = new();
	private ImmutableArray<Func<WhatsAppEvent, CancellationToken, ValueTask>> _handlers = [];
	private ImmutableArray<Action<HandlerError>> _errorHandlers = [];
	private Task _reader = Task.CompletedTask;
	private Task _ephemeralReader = Task.CompletedTask;
	private int _started;
	private int _disposed;

	public SessionEvents(WhatsmeowSidecar sidecar, CallInvoker invoker, SessionId sessionId, TimeProvider timeProvider, ILogger log)
	{
		_sidecar = sidecar;
		_client = new EventService.EventServiceClient(invoker);
		_sessionId = sessionId;
		_timeProvider = timeProvider;
		_log = log;
		_dispatcher = new Dispatcher(_channel.Reader, AckAsync, timeProvider, log);
		_ephemeralDispatcher = new Dispatcher(_ephemeralChannel.Reader, ackAsync: null, timeProvider, log);
	}

	/// <summary>The client was deleted or its sidecar disposed: no event comes any more.</summary>
	public bool IsDisposed => _disposed != 0;

	/// <summary>Starts reading the session's events; calling it again does nothing.</summary>
	public void Start()
	{
		if (_disposed != 0 || Interlocked.Exchange(ref _started, 1) != 0)
			return;

		_dispatcher.Start();
		_ephemeralDispatcher.Start();
		_reader = ReadDurableAsync(_stopping.Token);
		_ephemeralReader = ReadEphemeralAsync(_stopping.Token);
	}

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

	/// <summary>Stops reading, lets the dispatcher stop its handlers, and acks what they finished (ARCHITECTURE §8.4).</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		await _stopping.CancelAsync().ConfigureAwait(false);
		try
		{
			await _reader.ConfigureAwait(false);
			await _ephemeralReader.ConfigureAwait(false);
		}
		finally
		{
			await _dispatcher.DisposeAsync().ConfigureAwait(false);
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

	/// <summary>
	/// Reads the durable stream into its channel. When the sidecar goes away it waits for the next launch and opens the
	/// stream again after the highest seq it received, so what it holds is neither lost nor repeated; the events it did
	/// not get come from the cursor (ARCHITECTURE §8.5).
	/// </summary>
	private Task ReadDurableAsync(CancellationToken cancellationToken)
	{
		ulong highest = 0;
		return ReadAsync(
			ct => _client.StreamEvents(new StreamEventsRequest { SessionId = _sessionId.Value, Consumer = Consumer, AfterSeq = highest }, cancellationToken: ct),
			async (response, ct) =>
			{
				// The sidecar sends only seqs above after_seq, in order (events.proto).
				highest = response.Seq;
				await _channel.Writer.WriteAsync(ToWork(response), ct).ConfigureAwait(false);
			},
			cancellationToken
		);
	}

	/// <summary>
	/// Reads the ephemeral stream into its channel, numbering the events as they arrive, for their dispatcher's order
	/// only. A stream opened again starts with what the sidecar still keeps that no stream got (events.proto); what the
	/// sidecar dropped meanwhile arrives as <see cref="EphemeralEventsDropped"/>.
	/// </summary>
	private Task ReadEphemeralAsync(CancellationToken cancellationToken)
	{
		ulong arrived = 0;
		return ReadAsync(
			ct => _client.StreamEphemeralEvents(new StreamEphemeralEventsRequest { SessionId = _sessionId.Value }, cancellationToken: ct),
			async (response, ct) => await _ephemeralChannel.Writer.WriteAsync(ToWork(++arrived, response), ct).ConfigureAwait(false),
			cancellationToken
		);
	}

	/// <summary>
	/// Reads a stream of the session until the stop, opening it again when it ends: after the sidecar's next launch when
	/// it went away, and for good when the session was deleted or the consumer replaced.
	/// </summary>
	private async Task ReadAsync<TResponse>(
		Func<CancellationToken, AsyncServerStreamingCall<TResponse>> open,
		Func<TResponse, CancellationToken, ValueTask> receive,
		CancellationToken cancellationToken
	)
	{
		try
		{
			while (true)
			{
				var launch = _sidecar.Launches;
				using var call = open(cancellationToken);
				try
				{
					while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
						await receive(call.ResponseStream.Current, cancellationToken).ConfigureAwait(false);

					// The contract never ends a stream with OK; open it again.
					await Task.Delay(s_retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
				}
				catch (RpcException) when (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				catch (RpcException e) when (RpcErrors.IsUnreachable(e))
				{
					await _sidecar.WaitForLaunchAsync(launch, cancellationToken).ConfigureAwait(false);
				}
				catch (RpcException e) when (RpcErrors.Code(e) is WhatsmeowErrorCode.ConsumerReplaced or WhatsmeowErrorCode.SessionNotFound or WhatsmeowErrorCode.InvalidArgument)
				{
					// Replaced by another client of the same consumer, or the session was deleted: final.
					EventLog.StreamEnded(_log, e, _sessionId.Value);
					return;
				}
				catch (RpcException e)
				{
					EventLog.StreamFailed(_log, e, _sessionId.Value, s_retryDelay);
					await Task.Delay(s_retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					// A call can end with no status at all, as one to a sidecar that is gone does [observed:
					// WatchDownTests]. The same sidecar may still be up, so it does not wait for a launch: it opens
					// again, after a pause if none happened since the call opened.
					if (_sidecar.Launches == launch)
						await Task.Delay(s_retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Stopping.
		}
		catch (ObjectDisposedException)
		{
			// The sidecar was disposed while the reader waited for its next launch.
		}
	}

	private Work ToWork(StreamEventsResponse response)
	{
		WhatsAppEvent? e;
		try
		{
			e = EventMapping.ToEvent(_sessionId, response);
		}
#pragma warning disable CA1031 // Justification: an event this client cannot map is skipped and acked, never the end of the stream
		catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
		{
			EventLog.EventUnreadable(_log, exception, response.Seq, _sessionId.Value);
			e = null;
		}

		return new Work
		{
			Seq = response.Seq,
			Lane = EventMapping.Lane(response),

			// A payload case this client does not know is skipped, and still acked (events.proto).
			RunAsync = e is null ? static _ => ValueTask.CompletedTask : ct => RunAsync(e, ct),
		};
	}

	/// <summary>The ephemeral event as the dispatcher's work: <paramref name="arrived"/> orders it, and nothing acks it.</summary>
	private Work ToWork(ulong arrived, StreamEphemeralEventsResponse response)
	{
		WhatsAppEvent? e;
		try
		{
			e = EventMapping.ToEvent(_sessionId, response);

			// A payload case or a state this client does not know is skipped.
			if (e is null)
				EventLog.EphemeralEventSkipped(_log, response.PayloadCase.ToString(), _sessionId.Value);
		}
#pragma warning disable CA1031 // Justification: an ephemeral event this client cannot map is skipped, never the end of the stream
		catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
		{
			EventLog.EphemeralEventUnreadable(_log, exception, _sessionId.Value);
			e = null;
		}

		return new Work
		{
			Seq = arrived,
			Lane = EventMapping.Lane(response),
			RunAsync = e is null ? static _ => ValueTask.CompletedTask : ct => RunAsync(e, ct),
		};
	}

	private async Task AckAsync(ulong seq, CancellationToken cancellationToken)
	{
		_ = await RpcErrors
			.CallAsync(_client.AckEventsAsync(new AckEventsRequest { SessionId = _sessionId.Value, Consumer = Consumer, UpToSeq = seq }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		EventLog.Acked(_log, _sessionId.Value, seq);
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
