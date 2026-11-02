using Grpc.Core;
using Grpc.Core.Interceptors;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Telemetry;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Counts every failed sidecar call by its error code, as <see cref="RpcErrors"/> maps it, in the one place every call
/// passes: unary calls, client streams and server streams alike, the library's own calls included. A cancelled call is
/// not counted: with <c>ThrowOperationCanceledOnCancellation</c> the caller's cancellation is no <see cref="RpcException"/>,
/// and a <see cref="StatusCode.Cancelled"/> one is skipped.
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
		return new AsyncUnaryCall<TResponse>(CountedAsync(call.ResponseAsync), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
	}

	public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
		ClientInterceptorContext<TRequest, TResponse> context,
		AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation
	)
	{
		var call = continuation(context);
		return new AsyncClientStreamingCall<TRequest, TResponse>(
			call.RequestStream,
			CountedAsync(call.ResponseAsync),
			call.ResponseHeadersAsync,
			call.GetStatus,
			call.GetTrailers,
			call.Dispose
		);
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

	private async Task<TResponse> CountedAsync<TResponse>(Task<TResponse> response)
	{
		try
		{
			return await response.ConfigureAwait(false);
		}
		catch (RpcException e)
		{
			Count(e);
			throw;
		}
	}

	private void Count(RpcException exception)
	{
		if (exception.StatusCode is not StatusCode.Cancelled)
			metrics.Failed(RpcErrors.Code(exception));
	}

	private sealed class CountedReader<TResponse>(IAsyncStreamReader<TResponse> inner, ErrorCounter counter) : IAsyncStreamReader<TResponse>
	{
		public TResponse Current => inner.Current;

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			try
			{
				return await inner.MoveNext(cancellationToken).ConfigureAwait(false);
			}
			catch (RpcException e)
			{
				counter.Count(e);
				throw;
			}
		}
	}
}
