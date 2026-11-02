using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// WhatsApp refused the request with an error no other exception covers, for example because the session is not an
/// admin of the group, or not in it. Nothing was done.
/// </summary>
public sealed class WhatsAppErrorException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public WhatsAppErrorException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public WhatsAppErrorException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public WhatsAppErrorException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	/// <summary>
	/// WhatsApp's numeric error code, HTTP-like (403 forbidden, 404 not found, 406 not acceptable); 0 when WhatsApp sent
	/// none. Settable so a test can build the exception the library throws (ADR 0036).
	/// </summary>
	public int Code { get; init; }

	[DoesNotReturn]
	internal static void Throw(RpcException exception, int code) =>
		throw new WhatsAppErrorException($"WhatsApp refused the request with code {code}: {exception.Status.Detail}", exception)
		{
			ErrorCode = WhatsmeowErrorCode.WhatsAppError,
			Code = code,
		};
}
