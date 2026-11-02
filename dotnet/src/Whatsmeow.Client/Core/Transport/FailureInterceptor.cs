using Grpc.Core;
using Grpc.Core.Interceptors;
using Whatsmeow.Core.Telemetry;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Sees every failed call once, in the one place its calls pass: unary calls, client streams and server streams alike.
/// A unary or client stream call is returned as it is, its response watched on the side; a server stream reports its
/// first failed read, however often it is read after it, and never a read its own token cancelled.
/// </summary>
internal abstract class FailureInterceptor : Interceptor
{
	public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
		TRequest request,
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncUnaryCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(request, context);
		WatchResponse(call.ResponseAsync, context.Options.CancellationToken);
		return call;
	}

	public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(context);
		WatchResponse(call.ResponseAsync, context.Options.CancellationToken);
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
			new WatchedReader<TResponse>(call.ResponseStream, this, context.Options.CancellationToken),
			call.ResponseHeadersAsync,
			call.GetStatus,
			call.GetTrailers,
			call.Dispose
		);
	}

	/// <summary>
	/// A call failed with <paramref name="exception"/>, or its response was cancelled with none (null);
	/// <paramref name="caller"/> is the token the call was made with.
	/// </summary>
	protected abstract void Failed(Exception? exception, CancellationToken caller);

	/// <summary>
	/// Reports the response's failure as it completes, before the caller's await resumes; reading its exception observes
	/// it, as the caller's await does too. A response cancelled with no exception is reported as the caller's await sees it.
	/// </summary>
	private void WatchResponse(Task response, CancellationToken caller) =>
		_ = response.ContinueWith(
			static (failed, state) =>
			{
				var (interceptor, caller) = ((FailureInterceptor, CancellationToken))state!;
				interceptor.Failed(failed.Exception?.InnerException, caller);
			},
			(this, caller),
			CancellationToken.None,
			TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default
		);

	private sealed class WatchedReader<TResponse>(IAsyncStreamReader<TResponse> inner, FailureInterceptor interceptor, CancellationToken caller) : IAsyncStreamReader<TResponse>
	{
		private int _reported;

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
				if (!Tracing.IsCancellation(e, cancellationToken) && Interlocked.Exchange(ref _reported, 1) == 0)
					interceptor.Failed(e, caller);
				throw;
			}
		}
	}
}
