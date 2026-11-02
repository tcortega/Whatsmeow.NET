using Grpc.Core;
using Grpc.Core.Interceptors;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Telemetry;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Counts every failed sidecar call in the one place every call passes: unary calls, client streams and server streams
/// alike, the library's own calls included. A unary or client stream call is returned as it is, its response counted
/// on the side; a server stream counts its failure once, however often it is read after it. A status is counted by
/// its error code, as <see cref="RpcErrors"/> maps it; a cancellation the caller's token did not cause, as a call cut
/// by a sidecar that died, by its exception's type. The caller's own cancellation is not counted (ADR 0037).
/// </summary>
internal sealed class ErrorCounter(ClientMetrics metrics) : Interceptor
{
	public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
		TRequest request,
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncUnaryCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(request, context);
		CountWhenFailed(call.ResponseAsync, context.Options.CancellationToken);
		return call;
	}

	public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(context);
		CountWhenFailed(call.ResponseAsync, context.Options.CancellationToken);
		return call;
	}

	public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
		TRequest request,
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(request, context);
		return new AsyncServerStreamingCall<TResponse>(
			new CountedReader<TResponse>(call.ResponseStream, this, context.Options.CancellationToken),
			call.ResponseHeadersAsync,
			call.GetStatus,
			call.GetTrailers,
			call.Dispose
		);
	}

	/// <summary>
	/// Counts the response's failure as it completes, before the caller's await resumes; reading its exception observes it,
	/// as the caller's await does too. A response cancelled with no exception is counted as the caller's await sees it.
	/// </summary>
	private void CountWhenFailed(Task response, CancellationToken caller) =>
		_ = response.ContinueWith(
			static (failed, state) =>
			{
				var (counter, caller) = ((ErrorCounter, CancellationToken))state!;
				counter.Count(failed.Exception?.InnerException, caller);
			},
			(this, caller),
			CancellationToken.None,
			TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default
		);

	private void Count(Exception? exception, CancellationToken caller)
	{
		if (Tracing.IsCancellation(exception, caller))
			return;

		var type = exception switch
		{
			RpcException e => Tracing.Name(RpcErrors.Code(e)),
			OperationCanceledException e => Tracing.ErrorType(e),
			null => typeof(TaskCanceledException).FullName,
			_ => null,
		};
		if (type is not null)
			metrics.Failed(type);
	}

	private sealed class CountedReader<TResponse>(IAsyncStreamReader<TResponse> inner, ErrorCounter counter, CancellationToken caller) : IAsyncStreamReader<TResponse>
	{
		private int _counted;

		public TResponse Current => inner.Current;

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			try
			{
				return await inner.MoveNext(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e) when (e is RpcException or OperationCanceledException)
			{
				// The read's token is the caller's too.
				if (!Tracing.IsCancellation(e, cancellationToken) && Interlocked.Exchange(ref _counted, 1) == 0)
					counter.Count(e, caller);
				throw;
			}
		}
	}
}
