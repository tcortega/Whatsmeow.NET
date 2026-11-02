namespace Whatsmeow;

/// <summary>
/// The sidecar is not the version this client was built with, or speaks a contract revision it does not support.
/// </summary>
public sealed class SidecarVersionMismatchException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public SidecarVersionMismatchException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public SidecarVersionMismatchException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public SidecarVersionMismatchException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	[DoesNotReturn]
	internal static void Throw(string clientVersion, string sidecarVersion, int sidecarProtocol, int minProtocol, int maxProtocol) =>
		throw new SidecarVersionMismatchException(
			$"The sidecar is version {sidecarVersion} with protocol {sidecarProtocol}; this client is version {clientVersion} and needs the same version and a protocol from {minProtocol} to {maxProtocol}."
		);

	/// <summary>An external sidecar's protocol is out of range; any version is accepted there (ADR 0046).</summary>
	[DoesNotReturn]
	internal static void ThrowProtocol(string sidecarVersion, int sidecarProtocol, int minProtocol, int maxProtocol) =>
		throw new SidecarVersionMismatchException(
			$"The external sidecar is version {sidecarVersion} with protocol {sidecarProtocol}; this client needs a protocol from {minProtocol} to {maxProtocol}."
		);
}
