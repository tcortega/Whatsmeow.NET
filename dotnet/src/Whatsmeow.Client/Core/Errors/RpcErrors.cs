using System.Collections.Frozen;
using Google.Rpc;
using Grpc.Core;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Core.Errors;

/// <summary>
/// Maps a failed sidecar call to the public exceptions (ARCHITECTURE §8.7). It maps on the ErrorInfo reason only,
/// never on the message, so <see cref="RpcException"/> never escapes the public API.
/// </summary>
internal static class RpcErrors
{
	private const string Domain = "whatsmeow.net";
	private const string Prefix = "ERROR_REASON_";

	/// <summary>The reasons as they travel, read from the contract's own <c>ErrorReason</c> descriptor (error.proto).</summary>
	private static readonly FrozenDictionary<string, ErrorReason> s_reasons = ErrorReflection.Descriptor
		.FindTypeByName<Google.Protobuf.Reflection.EnumDescriptor>(nameof(ErrorReason))
		.Values.ToFrozenDictionary(value => value.Name[Prefix.Length..], value => (ErrorReason)value.Number, StringComparer.Ordinal);

	[DoesNotReturn]
	public static void Throw(RpcException exception)
	{
		var reason = exception.GetRpcStatus()?.GetDetail<ErrorInfo>() is { Domain: Domain } info && s_reasons.TryGetValue(info.Reason, out var known)
			? known
			: (ErrorReason?)null;

		// Without an ErrorInfo, Unavailable comes from the client's own transport: the sidecar is not reachable.
		if (reason is ErrorReason.Unauthenticated or ErrorReason.SidecarShuttingDown || (reason is null && exception.StatusCode is StatusCode.Unavailable))
			SidecarUnavailableException.ThrowUnreachable(exception);

		WhatsmeowException.Throw(exception);
	}
}
