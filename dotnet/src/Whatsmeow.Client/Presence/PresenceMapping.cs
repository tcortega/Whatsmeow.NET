using V1 = Whatsmeow.Protocol.V1;

namespace Whatsmeow.Presence;

/// <summary>The presence.proto states as the public enums, both ways.</summary>
internal static class PresenceMapping
{
	/// <summary>The state to send; a value the enum does not define is unspecified, which the sidecar refuses.</summary>
	public static V1.PresenceState ToProto(PresenceState state) =>
		state switch
		{
			PresenceState.Available => V1.PresenceState.Available,
			PresenceState.Unavailable => V1.PresenceState.Unavailable,
			_ => V1.PresenceState.Unspecified,
		};

	/// <inheritdoc cref="ToProto(PresenceState)"/>
	public static V1.ChatPresenceState ToProto(ChatPresenceState state) =>
		state switch
		{
			ChatPresenceState.Typing => V1.ChatPresenceState.Typing,
			ChatPresenceState.Recording => V1.ChatPresenceState.Recording,
			ChatPresenceState.Paused => V1.ChatPresenceState.Paused,
			_ => V1.ChatPresenceState.Unspecified,
		};

	/// <summary>The state received; null for one this client does not know.</summary>
	public static PresenceState? ToState(V1.PresenceState state) =>
		state switch
		{
			V1.PresenceState.Available => PresenceState.Available,
			V1.PresenceState.Unavailable => PresenceState.Unavailable,
			V1.PresenceState.Unspecified => null,
			_ => null,
		};

	/// <inheritdoc cref="ToState(V1.PresenceState)"/>
	public static ChatPresenceState? ToState(V1.ChatPresenceState state) =>
		state switch
		{
			V1.ChatPresenceState.Typing => ChatPresenceState.Typing,
			V1.ChatPresenceState.Recording => ChatPresenceState.Recording,
			V1.ChatPresenceState.Paused => ChatPresenceState.Paused,
			V1.ChatPresenceState.Unspecified => null,
			_ => null,
		};
}
