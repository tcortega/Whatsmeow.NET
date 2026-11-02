using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// The base of every error this library raises. A failed sidecar call carries its gRPC error as the
/// <see cref="Exception.InnerException"/>, and the reason the sidecar sent as <see cref="ErrorCode"/>.
/// </summary>
public class WhatsmeowException : Exception
{
	/// <summary>Creates the exception.</summary>
	public WhatsmeowException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public WhatsmeowException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public WhatsmeowException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	/// <summary>
	/// Why the call failed; <see cref="WhatsmeowErrorCode.Unknown"/> when the sidecar sent no reason this client knows.
	/// Settable so a test can build the exception the library throws, for a fake to throw (ADR 0036).
	/// </summary>
	public WhatsmeowErrorCode ErrorCode { get; init; }

	[DoesNotReturn]
	internal static void ThrowUnmapped(RpcException exception, WhatsmeowErrorCode code) =>
		throw new WhatsmeowException($"The sidecar call failed: {exception.Status.Detail}", exception) { ErrorCode = code };
}
