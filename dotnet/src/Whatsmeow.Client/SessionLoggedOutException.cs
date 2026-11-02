using Grpc.Core;

namespace Whatsmeow;

/// <summary>The session was logged out; it has no device until it is paired again.</summary>
public sealed class SessionLoggedOutException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public SessionLoggedOutException()
	{
		ErrorCode = WhatsmeowErrorCode.LoggedOut;
	}

	/// <summary>Creates the exception with a message.</summary>
	public SessionLoggedOutException(string message)
		: base(message)
	{
		ErrorCode = WhatsmeowErrorCode.LoggedOut;
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public SessionLoggedOutException(string message, Exception? innerException)
		: base(message, innerException)
	{
		ErrorCode = WhatsmeowErrorCode.LoggedOut;
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new SessionLoggedOutException($"The session was logged out: {exception.Status.Detail}", exception);
}
