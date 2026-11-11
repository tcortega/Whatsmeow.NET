using System.Collections.Frozen;
using System.Globalization;
using Google.Rpc;
using Grpc.Core;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Core.Errors;

/// <summary>
/// Maps a failed sidecar call to the public exceptions (ARCHITECTURE §8.7). It maps on the ErrorInfo reason only,
/// never on the message, so <see cref="RpcException"/> never escapes the public API. A public stream call the sidecar's
/// crash or stop cuts off ends with a cancellation its caller did not cause; each one maps it next to its
/// <see cref="RpcException"/> catch (<see cref="SidecarUnavailableException.ThrowCallCut"/>).
/// </summary>
internal static class RpcErrors
{
	private const string Domain = "whatsmeow.net";
	private const string Prefix = "ERROR_REASON_";

	/// <summary>The reasons as they travel, read from the contract's own <c>ErrorReason</c> descriptor (error.proto).</summary>
	private static readonly FrozenDictionary<string, ErrorReason> s_reasons = ErrorReflection.Descriptor
		.FindTypeByName<Google.Protobuf.Reflection.EnumDescriptor>(nameof(ErrorReason))
		.Values.ToFrozenDictionary(value => value.Name[Prefix.Length..], value => (ErrorReason)value.Number, StringComparer.Ordinal);

	/// <summary>Awaits a unary call and maps its failure.</summary>
	public static async Task<T> CallAsync<T>(AsyncUnaryCall<T> call)
	{
		using (call)
		{
			try
			{
				return await call.ResponseAsync.ConfigureAwait(false);
			}
			catch (RpcException e)
			{
				Throw(e);
				throw;
			}
		}
	}

	/// <summary>
	/// The call failed because the sidecar cannot be reached: it is shutting down, the token is no longer its token,
	/// or the client's own transport failed (Unavailable without an ErrorInfo).
	/// </summary>
	public static bool IsUnreachable(RpcException exception) =>
		Reason(exception, out _) is ErrorReason.Unauthenticated or ErrorReason.SidecarShuttingDown
		|| (Reason(exception, out _) is null && exception.StatusCode is StatusCode.Unavailable);

	/// <summary>The error code of a failed call, as <see cref="Throw(RpcException)"/> would report it.</summary>
	public static WhatsmeowErrorCode Code(RpcException exception) => ToCode(Reason(exception, out _));

	/// <summary>The error code of a reason sent as a field (ConnectSessionResult.reason).</summary>
	public static WhatsmeowErrorCode Code(ErrorReason reason) => ToCode(reason);

	[DoesNotReturn]
	public static void Throw(RpcException exception)
	{
		var reason = Reason(exception, out var info);
		Throw(exception, reason, info);
	}

	/// <summary>A batch result carries only its reason, with no status detail or metadata (ADR 0053).</summary>
	[DoesNotReturn]
	public static void Throw(ErrorReason reason) =>
		Throw(new RpcException(new Grpc.Core.Status(StatusCode.Unknown, "Connecting a declared session failed.")), reason, info: null);

	[DoesNotReturn]
	private static void Throw(RpcException exception, ErrorReason? reason, ErrorInfo? info)
	{
		var code = ToCode(reason);

		if (reason is ErrorReason.Unauthenticated or ErrorReason.SidecarShuttingDown || IsUnreachable(exception))
			SidecarUnavailableException.ThrowUnreachable(exception, code);

		switch (reason)
		{
			case ErrorReason.InvalidArgument:
				WhatsmeowValidationException.Throw(exception);
				break;
			case ErrorReason.SessionNotFound:
				SessionNotFoundException.Throw(exception);
				break;
			case ErrorReason.NotLoggedIn:
				NotLoggedInException.Throw(exception);
				break;
			case ErrorReason.LoggedOut:
				SessionLoggedOutException.Throw(exception);
				break;
			case ErrorReason.NotConnected or ErrorReason.ConnectionFailed:
				NotConnectedException.Throw(exception, code);
				break;
			case ErrorReason.TemporarilyBanned:
				TemporarilyBannedException.Throw(exception, Until(info));
				break;
			case ErrorReason.OutcomeUnknown:
				OutcomeUnknownException.Throw(exception);
				break;
			case ErrorReason.AlreadyPaired or ErrorReason.PairingInProgress or ErrorReason.PairingTimedOut or ErrorReason.PairingFailed:
				PairingException.Throw(exception, code);
				break;
			case ErrorReason.RateLimited:
				RateLimitedException.Throw(exception);
				break;
			case ErrorReason.RecipientNotOnWhatsapp:
				RecipientNotFoundException.Throw(exception);
				break;
			case ErrorReason.MediaExpired:
				MediaExpiredException.Throw(exception);
				break;
			case ErrorReason.WhatsappError:
				WhatsAppErrorException.Throw(exception, WhatsAppCode(info));
				break;
			case null or ErrorReason.Unspecified or ErrorReason.Internal or ErrorReason.Unauthenticated or ErrorReason.SidecarShuttingDown
				or ErrorReason.ConsumerReplaced or ErrorReason.WhatsappTimedOut or ErrorReason.NoPushName or ErrorReason.InviteLinkInvalid
				or ErrorReason.PollSecretNotFound:
			default:
				break;
		}

		// The reasons the table maps to the base type keep their code here.
		WhatsmeowException.ThrowUnmapped(exception, code);
	}

	private static ErrorReason? Reason(RpcException exception, out ErrorInfo? info)
	{
		info = exception.GetRpcStatus()?.GetDetail<ErrorInfo>();
		return info is { Domain: Domain } && s_reasons.TryGetValue(info.Reason, out var known) ? known : null;
	}

	/// <summary>The ban's end from the <c>expireTime</c> metadata, RFC 3339 UTC (error.proto); null when it is unknown.</summary>
	private static DateTimeOffset? Until(ErrorInfo? info) =>
		info is not null && info.Metadata.TryGetValue("expireTime", out var value)
		&& DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until)
			? until
			: null;

	/// <summary>WhatsApp's numeric error code from the <c>code</c> metadata (error.proto); 0 when it is missing or malformed.</summary>
	private static int WhatsAppCode(ErrorInfo? info) =>
		info is not null && info.Metadata.TryGetValue("code", out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0;

	private static WhatsmeowErrorCode ToCode(ErrorReason? reason) =>
		reason switch
		{
			ErrorReason.Internal => WhatsmeowErrorCode.Internal,
			ErrorReason.InvalidArgument => WhatsmeowErrorCode.InvalidArgument,
			ErrorReason.Unauthenticated => WhatsmeowErrorCode.Unauthenticated,
			ErrorReason.SidecarShuttingDown => WhatsmeowErrorCode.SidecarShuttingDown,
			ErrorReason.SessionNotFound => WhatsmeowErrorCode.SessionNotFound,
			ErrorReason.NotLoggedIn => WhatsmeowErrorCode.NotLoggedIn,
			ErrorReason.LoggedOut => WhatsmeowErrorCode.LoggedOut,
			ErrorReason.AlreadyPaired => WhatsmeowErrorCode.AlreadyPaired,
			ErrorReason.PairingInProgress => WhatsmeowErrorCode.PairingInProgress,
			ErrorReason.PairingTimedOut => WhatsmeowErrorCode.PairingTimedOut,
			ErrorReason.PairingFailed => WhatsmeowErrorCode.PairingFailed,
			ErrorReason.NotConnected => WhatsmeowErrorCode.NotConnected,
			ErrorReason.ConnectionFailed => WhatsmeowErrorCode.ConnectionFailed,
			ErrorReason.TemporarilyBanned => WhatsmeowErrorCode.TemporarilyBanned,
			ErrorReason.ConsumerReplaced => WhatsmeowErrorCode.ConsumerReplaced,
			ErrorReason.RecipientNotOnWhatsapp => WhatsmeowErrorCode.RecipientNotOnWhatsApp,
			ErrorReason.RateLimited => WhatsmeowErrorCode.RateLimited,
			ErrorReason.OutcomeUnknown => WhatsmeowErrorCode.OutcomeUnknown,
			ErrorReason.WhatsappError => WhatsmeowErrorCode.WhatsAppError,
			ErrorReason.WhatsappTimedOut => WhatsmeowErrorCode.WhatsAppTimedOut,
			ErrorReason.NoPushName => WhatsmeowErrorCode.NoPushName,
			ErrorReason.MediaExpired => WhatsmeowErrorCode.MediaExpired,
			ErrorReason.InviteLinkInvalid => WhatsmeowErrorCode.InviteLinkInvalid,
			ErrorReason.PollSecretNotFound => WhatsmeowErrorCode.PollSecretNotFound,
			null or ErrorReason.Unspecified => WhatsmeowErrorCode.Unknown,
			_ => WhatsmeowErrorCode.Unknown,
		};
}
