using Grpc.Core;
using Whatsmeow.Core.Dispatching;
using Whatsmeow.Core.Errors;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Events;

/// <summary>
/// One session's events in this process (ARCHITECTURE §8.4): the reader of <c>StreamEvents</c>, which only decodes
/// and writes to a bounded channel, the <see cref="Dispatcher"/> behind it, and the handlers it runs, which are the
/// <see cref="EventPipeline"/>'s. The ephemeral events (presence, chat presence) come on <c>StreamEphemeralEvents</c>,
/// with a reader, channel and dispatcher of their own that never ack, so they never move, hold back or delay the
/// durable ones (ADR 0027). It starts on the first successful connect of the session in this process.
/// </summary>
internal sealed class SessionEvents : IAsyncDisposable
{
	/// <summary>How long the reader waits before it opens a stream again after a failure that is not the sidecar going away.</summary>
	private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

	private readonly WhatsmeowSidecar _sidecar;
	private readonly EventService.EventServiceClient _client;
	private readonly SessionId _sessionId;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _log;
	private readonly EventPipeline _pipeline;

	public SessionEvents(WhatsmeowSidecar sidecar, CallInvoker invoker, SessionId sessionId, TimeProvider timeProvider, ILogger log)
	{
		_sidecar = sidecar;
		_client = new EventService.EventServiceClient(invoker);
		_sessionId = sessionId;
		_timeProvider = timeProvider;
		_log = log;
		_pipeline = new EventPipeline(sessionId, AckAsync, timeProvider, log, sidecar.Metrics);
	}

	/// <summary>The client was deleted or its sidecar disposed: no event comes any more.</summary>
	public bool IsDisposed => _pipeline.IsDisposed;

	/// <summary>Starts reading the session's events; calling it again does nothing.</summary>
	public void Start() => _pipeline.Start(ReadDurableAsync, ReadEphemeralAsync);

	public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent => _pipeline.On(handler);

	public IDisposable OnHandlerError(Action<HandlerError> handler) => _pipeline.OnHandlerError(handler);

	public Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
		where TEvent : WhatsAppEvent => _pipeline.WaitForAsync(predicate, timeout, cancellationToken);

	/// <summary>Stops reading, lets the dispatcher stop its handlers, and acks what they finished (ARCHITECTURE §8.4).</summary>
	public ValueTask DisposeAsync() => _pipeline.DisposeAsync();

	/// <summary><see cref="DisposeAsync"/>; once <paramref name="stopWaiting"/> is cancelled, it no longer waits for handlers that ignore their cancellation (ADR 0034).</summary>
	public Task StopAsync(CancellationToken stopWaiting) => _pipeline.StopAsync(stopWaiting);

	/// <summary>
	/// Reads the durable stream into its channel. When the sidecar goes away it waits for the next launch and opens the
	/// stream again after the highest seq it received, so what it holds is neither lost nor repeated; the events it did
	/// not get come from the cursor (ARCHITECTURE §8.5).
	/// </summary>
	private Task ReadDurableAsync(CancellationToken cancellationToken)
	{
		ulong highest = 0;
		return ReadAsync(
			ct => _client.StreamEvents(new StreamEventsRequest { SessionId = _sessionId.Value, Consumer = _sidecar.Consumer, AfterSeq = highest }, cancellationToken: ct),
			async (response, ct) =>
			{
				// The sidecar sends only seqs above after_seq, in order (events.proto).
				highest = response.Seq;
				await _pipeline.Durable.WriteAsync(ToWork(response), ct).ConfigureAwait(false);
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
			async (response, ct) => await _pipeline.Ephemeral.WriteAsync(ToWork(++arrived, response), ct).ConfigureAwait(false),
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
			e = EventMapping.ToEvent(_sessionId, response, _log);
		}
#pragma warning disable CA1031 // Justification: an event this client cannot map is skipped and acked, never the end of the stream
		catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
		{
			EventLog.EventUnreadable(_log, exception, response.Seq, _sessionId.Value);
			e = null;
		}

		// A payload case this client does not know is skipped, and still acked (events.proto).
		return _pipeline.ToWork(response.Seq, e);
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

		return _pipeline.ToWork(arrived, e);
	}

	private async Task AckAsync(ulong seq, CancellationToken cancellationToken)
	{
		_ = await RpcErrors
			.CallAsync(_client.AckEventsAsync(new AckEventsRequest { SessionId = _sessionId.Value, Consumer = _sidecar.Consumer, UpToSeq = seq }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		EventLog.Acked(_log, _sessionId.Value, seq);
	}
}
