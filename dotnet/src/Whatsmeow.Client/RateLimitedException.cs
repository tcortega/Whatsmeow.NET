using Grpc.Core;

namespace Whatsmeow;

/// <summary>WhatsApp rate limited the request; nothing was done. WhatsApp sends no delay to wait (ARCHITECTURE §8.7).</summary>
public sealed class RateLimitedException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public RateLimitedException()
	{
		ErrorCode = WhatsmeowErrorCode.RateLimited;
	}

	/// <summary>Creates the exception with a message.</summary>
	public RateLimitedException(string message)
		: base(message)
	{
		ErrorCode = WhatsmeowErrorCode.RateLimited;
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public RateLimitedException(string message, Exception? innerException)
		: base(message, innerException)
	{
		ErrorCode = WhatsmeowErrorCode.RateLimited;
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new RateLimitedException($"WhatsApp rate limited the request: {exception.Status.Detail}", exception);
}
