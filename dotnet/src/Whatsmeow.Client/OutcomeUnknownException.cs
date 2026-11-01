using Grpc.Core;

namespace Whatsmeow;

/// <summary>The request may have reached WhatsApp and no answer came back: it may or may not have taken effect. It is never retried automatically.</summary>
public sealed class OutcomeUnknownException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public OutcomeUnknownException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public OutcomeUnknownException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public OutcomeUnknownException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new OutcomeUnknownException($"The outcome is unknown: {exception.Status.Detail}", exception) { ErrorCode = WhatsmeowErrorCode.OutcomeUnknown };
}
