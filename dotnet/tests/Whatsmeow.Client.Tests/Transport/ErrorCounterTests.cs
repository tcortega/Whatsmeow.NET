using System.Diagnostics.Metrics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.Core.Telemetry;
using Whatsmeow.Core.Transport;

namespace Whatsmeow.Client.Tests.Transport;

/// <summary>
/// The error counter's contract (ADR 0037): a unary or client stream call comes back as the call's own response task,
/// counted once when it fails; a server stream counts its failure once, however often it is read; a cancelled call is
/// not counted. Each call is completed by the test, so the count is read once the failure happened.
/// </summary>
public sealed class ErrorCounterTests : IDisposable
{
	private static readonly Method<string, string> s_method = new(MethodType.Unary, "service", "method", Marshallers.StringMarshaller, Marshallers.StringMarshaller);
	private static readonly ClientInterceptorContext<string, string> s_context = new(s_method, host: null, new CallOptions());

	private readonly OneMeterFactory _meters = new();
	private readonly ClientMetrics _metrics;
	private readonly ErrorCounter _counter;
	private readonly MetricCollector<long> _errors;

	public ErrorCounterTests()
	{
		_metrics = new ClientMetrics(_meters);
		_counter = new ErrorCounter(_metrics);
		_errors = new MetricCollector<long>(_meters, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
	}

	[Fact(Timeout = 10_000)]
	public async Task Unary_Failing_ReturnsTheCallsOwnResponse_AndCountsItOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var response = new TaskCompletionSource<string>();
		var call = _counter.AsyncUnaryCall("request", s_context, (_, _) => UnaryCallAsync(response.Task));

		Assert.Same(response.Task, call.ResponseAsync);
		response.SetException(new RpcException(new Status(StatusCode.Internal, "boom")));

		_ = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync.WaitAsync(ct));
		Assert.Equal([1L], _errors.GetMeasurementSnapshot().Select(measurement => measurement.Value));
	}

	[Fact(Timeout = 10_000)]
	public async Task Unary_SucceedingOrCancelled_IsNotCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		var succeeding = _counter.AsyncUnaryCall("request", s_context, (_, _) => UnaryCallAsync(Task.FromResult("response")));
		var cancelled = _counter.AsyncUnaryCall("request", s_context, (_, _) => UnaryCallAsync(Task.FromException<string>(new RpcException(new Status(StatusCode.Cancelled, "cancelled")))));

		Assert.Equal("response", await succeeding.ResponseAsync.WaitAsync(ct));
		_ = await Assert.ThrowsAsync<RpcException>(() => cancelled.ResponseAsync.WaitAsync(ct));
		Assert.Empty(_errors.GetMeasurementSnapshot());
	}

	[Fact(Timeout = 10_000)]
	public async Task ClientStream_Failing_ReturnsTheCallsOwnResponse_AndCountsItOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var response = new TaskCompletionSource<string>();
		var call = _counter.AsyncClientStreamingCall(
			s_context,
			_ => new AsyncClientStreamingCall<string, string>(
				null!,
				response.Task,
				Task.FromResult(new Metadata()),
				static () => Status.DefaultSuccess,
				static () => [],
				static () => { }
			)
		);

		Assert.Same(response.Task, call.ResponseAsync);
		response.SetException(new RpcException(new Status(StatusCode.Internal, "boom")));

		_ = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync.WaitAsync(ct));
		Assert.Equal([1L], _errors.GetMeasurementSnapshot().Select(measurement => measurement.Value));
	}

	[Fact(Timeout = 10_000)]
	public async Task ServerStream_FailedBeforeItsFirstRead_CountsOnce_HoweverOftenItIsRead()
	{
		var ct = TestContext.Current.CancellationToken;
		var reader = new FailedReader(new RpcException(new Status(StatusCode.NotFound, "gone")));
		var call = _counter.AsyncServerStreamingCall(
			"request",
			s_context,
			(_, _) => new AsyncServerStreamingCall<string>(reader, Task.FromResult(new Metadata()), static () => Status.DefaultSuccess, static () => [], static () => { })
		);

		for (var read = 0; read < 3; read++)
			_ = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct));

		Assert.Equal(3, reader.Reads);
		Assert.Equal([1L], _errors.GetMeasurementSnapshot().Select(measurement => measurement.Value));
	}

	public void Dispose()
	{
		_errors.Dispose();
		_metrics.Dispose();
		_meters.Dispose();
	}

	private static AsyncUnaryCall<string> UnaryCallAsync(Task<string> response) =>
		new(response, Task.FromResult(new Metadata()), static () => Status.DefaultSuccess, static () => [], static () => { });

	/// <summary>A stream whose call already failed: every read throws its error.</summary>
	private sealed class FailedReader(RpcException error) : IAsyncStreamReader<string>
	{
		public int Reads { get; private set; }

		public string Current => throw new InvalidOperationException("no message was read");

		public Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			Reads++;
			return Task.FromException<bool>(error);
		}
	}

	/// <summary>Gives the one meter it creates a scope of its own, so the collector sees this test's counter only.</summary>
	private sealed class OneMeterFactory : IMeterFactory
	{
		private Meter? _meter;

		public Meter Create(MeterOptions options)
		{
			options.Scope = this;
			return _meter ??= new Meter(options);
		}

		public void Dispose() => _meter?.Dispose();
	}
}
