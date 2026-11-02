using Grpc.Core;
using Whatsmeow.Core.Errors;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Ends a connect epoch to an external sidecar when one of its calls is refused <c>Unauthenticated</c>: the sidecar
/// took a rotated token from its file, so every later call of the epoch, which carries the old one, would be refused
/// too. The epoch's end makes the client connect again with the token its file holds then (ADR 0046). Only this
/// epoch's calls pass here, so a late refusal of an older epoch never ends a newer one.
/// </summary>
internal sealed class TokenRefusal(CancellationTokenSource refused) : FailureInterceptor
{
	protected override void Failed(Exception? exception, CancellationToken caller)
	{
		if (exception is not RpcException e || RpcErrors.Code(e) is not WhatsmeowErrorCode.Unauthenticated)
			return;

		try
		{
			// Its callbacks end the epoch's watch; they run on the thread pool, never inside the failed call.
			_ = refused.CancelAsync();
		}
		catch (ObjectDisposedException)
		{
			// The epoch's channel is disposed: that epoch ended long ago.
		}
	}
}
