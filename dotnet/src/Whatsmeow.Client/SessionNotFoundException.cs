using Grpc.Core;

namespace Whatsmeow;

/// <summary>No session has the id; it was never created, or it was deleted.</summary>
public sealed class SessionNotFoundException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public SessionNotFoundException()
	{
		ErrorCode = WhatsmeowErrorCode.SessionNotFound;
	}

	/// <summary>Creates the exception with a message.</summary>
	public SessionNotFoundException(string message)
		: base(message)
	{
		ErrorCode = WhatsmeowErrorCode.SessionNotFound;
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public SessionNotFoundException(string message, Exception? innerException)
		: base(message, innerException)
	{
		ErrorCode = WhatsmeowErrorCode.SessionNotFound;
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new SessionNotFoundException($"The session does not exist: {exception.Status.Detail}", exception);
}
