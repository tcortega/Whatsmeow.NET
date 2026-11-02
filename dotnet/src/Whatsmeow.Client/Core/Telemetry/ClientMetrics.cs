using System.Diagnostics.Metrics;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Core.Telemetry;

/// <summary>
/// One sidecar's instruments (ARCHITECTURE §8.8), on a <see cref="Meter"/> named <see cref="WhatsmeowTelemetry.MeterName"/>
/// from the application's <see cref="IMeterFactory"/>, which owns it, or on one of its own, disposed with it.
/// </summary>
internal sealed class ClientMetrics : IDisposable
{
	private readonly Meter _meter;
	private readonly bool _ownsMeter;
	private readonly Counter<long> _sent;
	private readonly Counter<long> _errors;
	private readonly Counter<long> _restarts;

	public ClientMetrics(IMeterFactory? meterFactory)
	{
		var options = new MeterOptions(WhatsmeowTelemetry.MeterName) { Version = ClientVersion.Current };
		_meter = meterFactory?.Create(options) ?? new Meter(options);
		_ownsMeter = meterFactory is null;
		_sent = _meter.CreateCounter<long>("whatsmeow.messages.sent", "{message}", "Messages WhatsApp accepted from a send.");
		_errors = _meter.CreateCounter<long>("whatsmeow.errors", "{error}", "Failed calls to the sidecar, by error code; cancellations are not counted.");
		_restarts = _meter.CreateCounter<long>("whatsmeow.sidecar.restarts", "{restart}", "Sidecars started again after one exited on its own.");
	}

	public void MessageSent(SessionId sessionId) => _sent.Add(1, new KeyValuePair<string, object?>(Tracing.SessionIdTag, sessionId.Value));

	public void Failed(WhatsmeowErrorCode code) => _errors.Add(1, new KeyValuePair<string, object?>(Tracing.ErrorTypeTag, Tracing.Name(code)));

	public void Restarted() => _restarts.Add(1);

	/// <summary>Disposes the meter when it is this sidecar's own; one from a factory is the factory's.</summary>
	public void Dispose()
	{
		if (_ownsMeter)
			_meter.Dispose();
	}
}
