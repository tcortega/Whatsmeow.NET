using Grpc.Core;

namespace Whatsmeow;

/// <summary>A value or a request is invalid: an id that breaks its rule, or a request the sidecar rejected as malformed.</summary>
public sealed class WhatsmeowValidationException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public WhatsmeowValidationException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public WhatsmeowValidationException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public WhatsmeowValidationException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new WhatsmeowValidationException($"The request is invalid: {exception.Status.Detail}", exception) { ErrorCode = WhatsmeowErrorCode.InvalidArgument };
}
