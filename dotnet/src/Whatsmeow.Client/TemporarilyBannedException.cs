using Grpc.Core;

namespace Whatsmeow;

/// <summary>The account is temporarily banned.</summary>
public sealed class TemporarilyBannedException : WhatsmeowException
{
	/// <summary>Creates the exception.</summary>
	public TemporarilyBannedException()
	{
	}

	/// <summary>Creates the exception with a message.</summary>
	public TemporarilyBannedException(string message)
		: base(message)
	{
	}

	/// <summary>Creates the exception with a message and its cause.</summary>
	public TemporarilyBannedException(string message, Exception? innerException)
		: base(message, innerException)
	{
	}

	/// <summary>When the ban ends; null when WhatsApp sent no end.</summary>
	public DateTimeOffset? Until { get; internal init; }

	[DoesNotReturn]
	internal static void Throw(RpcException exception, DateTimeOffset? until) =>
		throw new TemporarilyBannedException($"The account is temporarily banned: {exception.Status.Detail}", exception)
		{
			ErrorCode = WhatsmeowErrorCode.TemporarilyBanned,
			Until = until,
		};
}
