using Google.Rpc;
using Grpc.Core;

namespace Whatsmeow.Core.Errors;

/// <summary>
/// Maps a failed sidecar call to the public exceptions (ARCHITECTURE §8.7). It maps on the ErrorInfo reason only,
/// never on the message, so <see cref="RpcException"/> never escapes the public API.
/// </summary>
internal static class RpcErrors
{
	private const string Domain = "whatsmeow.net";

	[DoesNotReturn]
	public static void Throw(RpcException exception)
	{
		var reason = exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: Domain } info ? info.Reason : null;

		// Without an ErrorInfo, Unavailable comes from the client's own transport: the sidecar is not reachable.
		if (reason is "UNAUTHENTICATED" or "SIDECAR_SHUTTING_DOWN" || (reason is null && exception.StatusCode is StatusCode.Unavailable))
			SidecarUnavailableException.ThrowUnreachable(exception);

		WhatsmeowException.Throw(exception);
	}
}
