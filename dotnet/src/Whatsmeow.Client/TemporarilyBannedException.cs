using Grpc.Core;

namespace Whatsmeow;

/// <summary>The account is temporarily banned.</summary>
public sealed class TemporarilyBannedException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public TemporarilyBannedException() => ErrorCode = WhatsmeowErrorCode.TemporarilyBanned;

	/// <summary>Creates the exception with a message.</summary>
	public TemporarilyBannedException(string message)
		: base(message) => ErrorCode = WhatsmeowErrorCode.TemporarilyBanned;

	/// <summary>Creates the exception with a message and its cause.</summary>
	public TemporarilyBannedException(string message, Exception? innerException)
		: base(message, innerException) => ErrorCode = WhatsmeowErrorCode.TemporarilyBanned;

	/// <summary>When the ban ends; null when WhatsApp sent no end. Settable so a test can build the exception the library throws (ADR 0036).</summary>
	public DateTimeOffset? Until { get; init; }

	[DoesNotReturn]
	internal static void Throw(RpcException exception, DateTimeOffset? until) =>
		throw new TemporarilyBannedException($"The account is temporarily banned: {exception.Status.Detail}", exception)
		{
			Until = until,
		};
}
