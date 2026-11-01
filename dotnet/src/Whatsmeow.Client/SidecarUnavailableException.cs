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
	internal static void ThrowUnreachable(RpcException exception, WhatsmeowErrorCode code) =>
		throw new SidecarUnavailableException($"The sidecar cannot be reached: {exception.Status.Detail}", exception) { ErrorCode = code };
}
