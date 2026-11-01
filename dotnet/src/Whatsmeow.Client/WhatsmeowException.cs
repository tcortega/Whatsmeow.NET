using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// The base of every error this library raises. A failed sidecar call carries its gRPC error as the
/// <see cref="Exception.InnerException"/>.
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

	[DoesNotReturn]
	internal static void Throw(RpcException exception) =>
		throw new WhatsmeowException($"The sidecar call failed: {exception.Status.Detail}", exception);
}
