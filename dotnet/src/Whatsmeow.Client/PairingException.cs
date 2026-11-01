using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// Pairing failed or could not start. <see cref="WhatsmeowException.ErrorCode"/> says why:
/// <see cref="WhatsmeowErrorCode.AlreadyPaired"/>, <see cref="WhatsmeowErrorCode.PairingInProgress"/>,
/// <see cref="WhatsmeowErrorCode.PairingTimedOut"/> or <see cref="WhatsmeowErrorCode.PairingFailed"/>.
/// </summary>
public sealed class PairingException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public PairingException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public PairingException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public PairingException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(RpcException exception, WhatsmeowErrorCode code) =>
		throw new PairingException($"Pairing failed: {exception.Status.Detail}", exception) { ErrorCode = code };
}
