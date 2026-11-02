using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Whatsmeow.Events;
using static Whatsmeow.Testing.Tests.Samples;

namespace Whatsmeow.Testing.Tests;

/// <summary>
/// The fake has no sidecar and no meter, so a raised event's handlers run with no span and record no measurement,
/// whoever listens to <see cref="WhatsmeowTelemetry"/> (ADR 0037). Nothing else in this suite publishes any.
/// </summary>
public sealed class TelemetryTests : IAsyncDisposable
{
	private readonly FakeWhatsAppClient _fake = new(Session);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task RaisedEvent_WithListeners_PublishesNoSpanAndNoMeasurement()
	{
		var ct = TestContext.Current.CancellationToken;
		var spans = new ConcurrentQueue<Activity>();
		var measurements = new ConcurrentQueue<string>();
		using var tracing = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == WhatsmeowTelemetry.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStarted = spans.Enqueue,
		};
		ActivitySource.AddActivityListener(tracing);
		using var metrics = new MeterListener
		{
			InstrumentPublished = static (instrument, listener) =>
			{
				if (instrument.Meter.Name == WhatsmeowTelemetry.MeterName)
					listener.EnableMeasurementEvents(instrument);
			},
		};
		metrics.SetMeasurementEventCallback<long>((instrument, _, _, _) => measurements.Enqueue(instrument.Name));
		metrics.SetMeasurementEventCallback<int>((instrument, _, _, _) => measurements.Enqueue(instrument.Name));
		metrics.SetMeasurementEventCallback<double>((instrument, _, _, _) => measurements.Enqueue(instrument.Name));
		metrics.Start();
		Activity? current = null;
		using var handler = _fake.On<MessageReceived>(
			(_, _) =>
			{
				current = Activity.Current;
				return ValueTask.CompletedTask;
			}
		);
		using var failing = _fake.On<MessageReceived>((_, _) => throw new InvalidOperationException("a handler bug"));

		await _fake.RaiseAsync(Message(Ana, "a1"), ct);

		Assert.Null(current);
		Assert.Empty(spans);
		Assert.Empty(measurements);
	}
}
