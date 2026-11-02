using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// WhatsApp no longer has the media (it answered 403, 404 or 410). <see cref="IWhatsAppClient.RequestMediaRetryAsync"/>
/// can ask the account's phone to upload it again.
/// </summary>
public sealed class MediaExpiredException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public MediaExpiredException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public MediaExpiredException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public MediaExpiredException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new MediaExpiredException($"The media expired: {exception.Status.Detail}", exception) { ErrorCode = WhatsmeowErrorCode.MediaExpired };
}
