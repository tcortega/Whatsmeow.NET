using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;

namespace EchoBot;

/// <summary>Logs each session's state as it changes: one session logged out leaves the other as it is.</summary>
public sealed partial class StateLog(ILogger<StateLog> logger) : IWhatsAppEventHandler<ConnectionStateChanged>
{
	public ValueTask HandleAsync(ConnectionStateChanged e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		StateChanged(logger, e.SessionId.Value, e.State.GetType().Name);
		return ValueTask.CompletedTask;
	}

	[LoggerMessage(20, LogLevel.Information, "Session {SessionId} is {State}")]
	private static partial void StateChanged(ILogger logger, string sessionId, string state);
}
