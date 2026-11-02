namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// What the events host (<c>dotnet/tests/SidecarHost/EventsHost.cs</c>) prints on stdout after its sidecar's process
/// id: a label as each handler starts, then "acked" and the last acked seq. The fake-mode and live kill tests read it
/// here, the one parser of that output.
/// </summary>
internal static class EventsHostOutput
{
	/// <summary>
	/// Reads the handlers' labels, in order, until the host printed its ack, and returns them with the acked seq.
	/// </summary>
	public static async Task<(IReadOnlyList<string> Handled, string Acked)> UntilAckedAsync(StreamReader output, CancellationToken cancellationToken)
	{
		var handled = new List<string>();
		while (true)
		{
			var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("the host ended");
			if (line.StartsWith(AckedPrefix, StringComparison.Ordinal))
				return (handled, line[AckedPrefix.Length..]);
			handled.Add(line);
		}
	}

	private const string AckedPrefix = "acked ";
}
