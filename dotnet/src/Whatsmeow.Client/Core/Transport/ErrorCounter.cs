using Grpc.Core;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Telemetry;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Counts every failed sidecar call in the one place every call passes, the library's own calls included. A status is
/// counted by its error code, as <see cref="RpcErrors"/> maps it; a cancellation the caller's token did not cause, a
/// call cut by a sidecar that crashed or stopped, as <see cref="WhatsmeowErrorCode.Unknown"/>, the code of the
/// <see cref="SidecarUnavailableException"/> its caller gets. The caller's own cancellation is not counted (ADR 0037).
/// </summary>
internal sealed class ErrorCounter(ClientMetrics metrics) : FailureInterceptor
{
	protected override void Failed(Exception? exception, CancellationToken caller)
	{
		if (Tracing.IsCancellation(exception, caller))
			return;

		var type = exception switch
		{
			RpcException e => Tracing.Name(RpcErrors.Code(e)),
			OperationCanceledException or null => Tracing.Name(WhatsmeowErrorCode.Unknown),
			_ => null,
		};
		if (type is not null)
			metrics.Failed(type);
	}
}
