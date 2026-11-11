using System.Diagnostics;
using System.Diagnostics.Metrics;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Core.Telemetry;

/// <summary>
/// One sidecar's instruments (ARCHITECTURE §8.8), on a <see cref="Meter"/> named <see cref="WhatsmeowTelemetry.MeterName"/>
/// from the application's <see cref="IMeterFactory"/>, which owns it, or on one of its own, disposed with it.
/// </summary>
internal sealed class ClientMetrics : IDisposable
{
	/// <summary>Seconds, as ASP.NET Core's request duration buckets them: the default buckets are for milliseconds.</summary>
	private static readonly double[] s_durationBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

	private readonly Meter _meter;
	private readonly bool _ownsMeter;
	private readonly Counter<long> _sent;
	private readonly Counter<long> _errors;
	private readonly Counter<long> _restarts;
	private readonly Counter<long> _received;
	private readonly Histogram<double> _handlerDuration;
	private readonly UpDownCounter<int> _queueLength;

	public ClientMetrics(IMeterFactory? meterFactory)
	{
		var options = new MeterOptions(WhatsmeowTelemetry.MeterName) { Version = ClientVersion.Current };
		_meter = meterFactory?.Create(options) ?? new Meter(options);
		_ownsMeter = meterFactory is null;
		_sent = _meter.CreateCounter<long>("whatsmeow.messages.sent", "{message}", "Messages WhatsApp accepted from a send.");
		_errors = _meter.CreateCounter<long>("whatsmeow.errors", "{error}", "Failed calls to the sidecar, by error code, a call the sidecar's crash or stop cut off as Unknown; those the caller cancelled are not counted.");
		_restarts = _meter.CreateCounter<long>("whatsmeow.sidecar.restarts", "{restart}", "Sidecars started again after one exited on its own.");
		_received = _meter.CreateCounter<long>("whatsmeow.messages.received", "{message}", "Message events that arrived from the sidecar; a redelivery counts again.");
		_handlerDuration = _meter.CreateHistogram(
			"whatsmeow.handler.duration",
			"s",
			"How long one handler ran for one event, a handler that threw included.",
			tags: null,
			new InstrumentAdvice<double> { HistogramBucketBoundaries = s_durationBuckets }
		);
		_queueLength = _meter.CreateUpDownCounter<int>("whatsmeow.dispatcher.queue.length", "{event}", "Events a session's dispatchers received and have not finished; those still in the reader's channel are not counted.");
	}

	public void MessageSent(SessionId sessionId) => _sent.Add(1, new KeyValuePair<string, object?>(Tracing.SessionIdTag, sessionId.Value));

	/// <summary>A failed call, by its <c>error.type</c>: its error code, or the type of the exception it ended with.</summary>
	public void Failed(string errorType) => _errors.Add(1, new KeyValuePair<string, object?>(Tracing.ErrorTypeTag, errorType));

	public void Restarted() => _restarts.Add(1);

	public void MessageReceived(SessionId sessionId) => _received.Add(1, new KeyValuePair<string, object?>(Tracing.SessionIdTag, sessionId.Value));

	/// <summary>One handler of a <paramref name="eventType"/> event ran for <paramref name="duration"/>; <paramref name="errorType"/> when it threw.</summary>
	public void HandlerRan(SessionId sessionId, string eventType, TimeSpan duration, string? errorType)
	{
		if (!_handlerDuration.Enabled)
			return;

		var tags = new TagList { { Tracing.SessionIdTag, sessionId.Value }, { Tracing.EventTypeTag, eventType } };
		if (errorType is not null)
			tags.Add(Tracing.ErrorTypeTag, errorType);
		_handlerDuration.Record(duration.TotalSeconds, tags);
	}

	public void QueueChanged(SessionId sessionId, int delta) => _queueLength.Add(delta, new KeyValuePair<string, object?>(Tracing.SessionIdTag, sessionId.Value));

	/// <summary>Disposes the meter when it is this sidecar's own; one from a factory is the factory's.</summary>
	public void Dispose()
	{
		if (_ownsMeter)
			_meter.Dispose();
	}
}
