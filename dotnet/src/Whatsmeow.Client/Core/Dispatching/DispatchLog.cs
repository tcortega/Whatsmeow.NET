namespace Whatsmeow.Core.Dispatching;

internal static partial class DispatchLog
{
	[LoggerMessage(20, LogLevel.Warning, "Acking the events up to {Seq} failed; it is tried again on the next tick and at the stop")]
	public static partial void AckFailed(ILogger logger, Exception exception, ulong seq);

	[LoggerMessage(21, LogLevel.Warning, "The last ack did not finish within {Timeout}; the unacked events come again")]
	public static partial void FinalAckTimedOut(ILogger logger, TimeSpan timeout);

	[LoggerMessage(22, LogLevel.Error, "Running the handlers of event {Seq} failed; it counts as handled")]
	public static partial void RunFailed(ILogger logger, Exception exception, ulong seq);

	[LoggerMessage(24, LogLevel.Error, "Running the handlers of an ephemeral event failed; it counts as handled")]
	public static partial void UnackedRunFailed(ILogger logger, Exception exception);

	[LoggerMessage(
		23,
		LogLevel.Warning,
		"The handlers of event {Seq} have not finished for {After}, and {Waiting} events wait behind it to be acked; they come again in the next process if this one stops first"
	)]
	public static partial void WatermarkStuck(ILogger logger, ulong seq, int waiting, TimeSpan after);
}
