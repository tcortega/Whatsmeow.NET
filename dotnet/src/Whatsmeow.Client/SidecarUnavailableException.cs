using Grpc.Core;

namespace Whatsmeow;

/// <summary>
/// The sidecar could not be found, did not start, or cannot be reached right now.
/// </summary>
public sealed class SidecarUnavailableException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public SidecarUnavailableException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public SidecarUnavailableException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public SidecarUnavailableException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void ThrowNotFound(IReadOnlyList<string> pathsTried) =>
		throw new SidecarUnavailableException(
			$"The sidecar binary was not found. Paths tried: {string.Join(", ", pathsTried)}."
		);

	[DoesNotReturn]
	internal static void ThrowStartFailed(string reason, IReadOnlyList<string> stderrTail, Exception? innerException = null) =>
		throw new SidecarUnavailableException(
			stderrTail.Count == 0
				? $"The sidecar did not start: {reason}. It wrote nothing to stderr."
				: $"The sidecar did not start: {reason}. The last lines it wrote to stderr:{Environment.NewLine}{string.Join(Environment.NewLine, stderrTail)}",
			innerException
		);

	[DoesNotReturn]
	internal static void ThrowTokenUnreadable(string path, string reason, Exception? innerException) =>
		throw new SidecarUnavailableException($"The sidecar's token file {path} cannot be used: {reason}.", innerException);

	[DoesNotReturn]
	internal static void ThrowTokenRefused(string address, SidecarUnavailableException exception) =>
		throw new SidecarUnavailableException($"The sidecar at {address} refused the token: its --token-file and this client's TokenPath do not hold the same token.", exception)
		{
			ErrorCode = exception.ErrorCode,
		};

	[DoesNotReturn]
	internal static void ThrowNotAnswering(string address, TimeSpan startupTimeout, Exception? innerException) =>
		throw new SidecarUnavailableException($"The sidecar at {address} did not answer within {startupTimeout}.", innerException);

	[DoesNotReturn]
	internal static void ThrowUnreachable(RpcException exception, WhatsmeowErrorCode code) =>
		throw new SidecarUnavailableException($"The sidecar cannot be reached: {exception.Status.Detail}", exception) { ErrorCode = code };

	/// <summary>
	/// The sidecar's crash or stop cut a call off, which grpc-dotnet ends with a cancellation the caller did not cause;
	/// no reason came with it, as with a transport failure (<c>RpcErrors.IsUnreachable</c>).
	/// </summary>
	[DoesNotReturn]
	internal static void ThrowCallCut(OperationCanceledException exception) =>
		throw new SidecarUnavailableException("The sidecar stopped during the call.", exception) { ErrorCode = WhatsmeowErrorCode.Unknown };
}
