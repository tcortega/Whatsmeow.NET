using Microsoft.Extensions.Logging;
using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.Sessions;

namespace EchoBot;

/// <summary>Logs each session's state as it changes: one session logged out leaves the other as it is.</summary>
public sealed partial class StateLog(ILogger<StateLog> logger) : IWhatsAppEventHandler<SessionStateChanged>
{
	public ValueTask HandleAsync(SessionStateChanged e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		StateChanged(logger, e.SessionId.Value, Name(e.State));
		return ValueTask.CompletedTask;
	}

	private static string Name(SessionState state) =>
		state switch
		{
			SessionState.Created => nameof(SessionState.Created),
			SessionState.Pairing => nameof(SessionState.Pairing),
			SessionState.Connecting => nameof(SessionState.Connecting),
			SessionState.Connected => nameof(SessionState.Connected),
			SessionState.CaughtUp => nameof(SessionState.CaughtUp),
			SessionState.Reconnecting => nameof(SessionState.Reconnecting),
			SessionState.Disconnected => nameof(SessionState.Disconnected),
			SessionState.LoggedOut => nameof(SessionState.LoggedOut),
			SessionState.Replaced => nameof(SessionState.Replaced),
			SessionState.TempBanned => nameof(SessionState.TempBanned),
			SessionState.Outdated => nameof(SessionState.Outdated),
			SessionState.Error => nameof(SessionState.Error),
			_ => nameof(SessionState.Unknown),
		};

	[LoggerMessage(20, LogLevel.Information, "Session {SessionId} is {State}")]
	private static partial void StateChanged(ILogger logger, string sessionId, string state);
}
