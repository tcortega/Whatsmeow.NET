using System.Threading.Channels;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;

namespace Whatsmeow.LiveTests;

/// <summary>Every message a client's handler received, with the time since <see cref="Origin"/>, in order.</summary>
internal sealed class Arrivals : IDisposable
{
	private readonly Channel<(TimeSpan At, MessageReceived Message)> _arrived = Channel.CreateUnbounded<(TimeSpan, MessageReceived)>();
	private readonly IDisposable _registration;

	/// <param name="before">Runs in the handler before the message is recorded, for a test that holds a handler.</param>
	public Arrivals(IWhatsAppClient client, Func<MessageReceived, CancellationToken, Task>? before = null) =>
		_registration = client.On<MessageReceived>(
			async (e, ct) =>
			{
				if (before is not null)
					await before(e, ct).ConfigureAwait(false);
				_ = _arrived.Writer.TryWrite((LiveClock.Time.GetElapsedTime(Origin), e));
			}
		);

	/// <summary>When this started, a <see cref="TimeProvider.GetTimestamp"/>.</summary>
	public long Origin { get; } = LiveClock.Time.GetTimestamp();

	/// <summary>
	/// Reads the burst's messages until every text in <paramref name="expected"/> came, for at most
	/// <paramref name="bound"/>, then <paramref name="grace"/> more for repeats. It returns what came, also when the bound
	/// ended first: the delivery checks then report what was lost.
	/// </summary>
	public async Task<IReadOnlyList<(TimeSpan At, MessageReceived Message)>> CollectAsync(
		Burst burst,
		IReadOnlyCollection<string> expected,
		TimeSpan bound,
		TimeSpan grace,
		CancellationToken cancellationToken
	)
	{
		var ours = new List<(TimeSpan, MessageReceived)>();
		var missing = new HashSet<string>(expected, StringComparer.Ordinal);
		await ReadAsync(bound, () => missing.Count == 0).ConfigureAwait(false);
		await ReadAsync(grace, () => false).ConfigureAwait(false);
		return ours;

		async Task ReadAsync(TimeSpan limit, Func<bool> done)
		{
			using var bounded = LiveClock.Bound(limit, cancellationToken);
			try
			{
				while (!done())
				{
					var arrival = await _arrived.Reader.ReadAsync(bounded.Token).ConfigureAwait(false);
					if (DeliveryChecks.TextOf(arrival.Message) is not { } text || !burst.ByText.ContainsKey(text))
						continue;
					ours.Add(arrival);
					_ = missing.Remove(text);
				}
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
		}
	}

	public void Dispose() => _registration.Dispose();
}
