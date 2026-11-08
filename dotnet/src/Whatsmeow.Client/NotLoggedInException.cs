using Grpc.Core;

namespace Whatsmeow;

/// <summary>The session has never been paired, so it cannot connect or log out.</summary>
public sealed class NotLoggedInException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public NotLoggedInException() => ErrorCode = WhatsmeowErrorCode.NotLoggedIn;

	/// <summary>Creates the exception with a message.</summary>
	public NotLoggedInException(string message)
		: base(message) => ErrorCode = WhatsmeowErrorCode.NotLoggedIn;

	/// <summary>Creates the exception with a message and its cause.</summary>
	public NotLoggedInException(string message, Exception? innerException)
		: base(message, innerException) => ErrorCode = WhatsmeowErrorCode.NotLoggedIn;

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new NotLoggedInException($"The session has never been paired: {exception.Status.Detail}", exception);
}
