using Grpc.Core;
using Grpc.Core.Interceptors;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Telemetry;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Counts every failed sidecar call by its error code, as <see cref="RpcErrors"/> maps it, in the one place every call
/// passes: unary calls, client streams and server streams alike, the library's own calls included. A unary or client
/// stream call is returned as it is, its response counted on the side; a server stream counts its failure once, however
/// often it is read after it. A cancelled call is not counted: with <c>ThrowOperationCanceledOnCancellation</c> the
/// caller's cancellation is no <see cref="RpcException"/>, and a <see cref="StatusCode.Cancelled"/> one is skipped.
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
		CountWhenFaulted(call.ResponseAsync);
		return call;
	}

	public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(context);
		CountWhenFaulted(call.ResponseAsync);
		return call;
	}

	public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
		TRequest request,
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(request, context);
		return new AsyncServerStreamingCall<TResponse>(new CountedReader<TResponse>(call.ResponseStream, this), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
	}

	/// <summary>
	/// Counts the response's failure as it completes, before the caller's await resumes; reading its exception observes it,
	/// as the caller's await does too.
	/// </summary>
	private void CountWhenFaulted(Task response) =>
		_ = response.ContinueWith(
			static (faulted, state) =>
			{
				if (faulted.Exception?.InnerException is RpcException e)
					((ErrorCounter)state!).Count(e);
			},
			this,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default
		);

	private void Count(RpcException exception)
	{
		if (exception.StatusCode is not StatusCode.Cancelled)
			metrics.Failed(RpcErrors.Code(exception));
	}

	private sealed class CountedReader<TResponse>(IAsyncStreamReader<TResponse> inner, ErrorCounter counter) : IAsyncStreamReader<TResponse>
	{
		private int _counted;

		public TResponse Current => inner.Current;

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			try
			{
				return await inner.MoveNext(cancellationToken).ConfigureAwait(false);
			}
			catch (RpcException e)
			{
				if (Interlocked.Exchange(ref _counted, 1) == 0)
					counter.Count(e);
				throw;
			}
		}
	}
}
