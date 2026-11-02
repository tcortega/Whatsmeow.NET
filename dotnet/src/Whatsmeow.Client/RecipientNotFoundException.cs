using Grpc.Core;

namespace Whatsmeow;

/// <summary>The user is not on WhatsApp: WhatsApp knows no account for the phone number. Nothing was done.</summary>
public sealed class RecipientNotFoundException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public RecipientNotFoundException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public RecipientNotFoundException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public RecipientNotFoundException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new RecipientNotFoundException($"The user is not on WhatsApp: {exception.Status.Detail}", exception)
		{
			ErrorCode = WhatsmeowErrorCode.RecipientNotOnWhatsApp,
		};
}
