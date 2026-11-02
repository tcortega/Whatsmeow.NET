using Grpc.Core;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// The invoker every call goes through, stable across sidecar restarts: it starts each call on the current launch's
/// channel, whose endpoint and token are fixed, so a call stays with the launch it started on and never reaches the
/// next one with this one's token (#85, ADR 0045). With no launch it starts the call on <c>none</c>, which never
/// reaches a sidecar.
/// </summary>
/// <param name="none">Where a call goes while no launch is current: before the first, and after the dispose.</param>
internal sealed class LaunchInvoker(CallInvoker none) : CallInvoker
{
	private volatile CallInvoker? _current;

	private CallInvoker Current => _current ?? none;

	/// <summary>Makes <paramref name="launch"/> the current launch's invoker, or none when null.</summary>
	public void Use(CallInvoker? launch) => _current = launch;

	public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
		Current.BlockingUnaryCall(method, host, options, request);

	public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
		Current.AsyncUnaryCall(method, host, options, request);

	public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
		Method<TRequest, TResponse> method,
		string? host,
		CallOptions options,
		TRequest request
	) =>
		Current.AsyncServerStreamingCall(method, host, options, request);

	public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
		Current.AsyncClientStreamingCall(method, host, options);

	public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
		Current.AsyncDuplexStreamingCall(method, host, options);
}
