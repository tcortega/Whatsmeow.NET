using Grpc.Core;

namespace Whatsmeow;

/// <summary>The session is not connected to WhatsApp right now, or opening the connection failed; nothing was done.</summary>
public sealed class NotConnectedException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public NotConnectedException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public NotConnectedException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public NotConnectedException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception, WhatsmeowErrorCode code) =>
		throw new NotConnectedException($"The session is not connected: {exception.Status.Detail}", exception) { ErrorCode = code };
}
