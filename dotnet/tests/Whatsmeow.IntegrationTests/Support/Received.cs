using System.Threading.Channels;
using Whatsmeow.Events;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>The events a client's handler received, read in order by condition.</summary>
internal sealed class Received<TEvent> : IDisposable
	where TEvent : WhatsAppEvent
{
	private readonly Channel<TEvent> _events = Channel.CreateUnbounded<TEvent>();
	private readonly IDisposable _registration;

	/// <param name="before">Runs in the handler before the event is recorded, for a test that holds a handler; it
	/// honours the token, so a failing test never keeps the shared sidecar from stopping.</param>
	public Received(IWhatsAppClient client, Func<TEvent, CancellationToken, Task>? before = null) =>
		_registration = client.On<TEvent>(
			async (e, ct) =>
			{
				if (before is not null)
					await before(e, ct).ConfigureAwait(false);
				_ = _events.Writer.TryWrite(e);
			}
		);

	/// <summary>The next event; the test's timeout ends the wait.</summary>
	public async Task<TEvent> NextAsync(CancellationToken cancellationToken) => await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

	/// <summary>The next <paramref name="count"/> events.</summary>
	public async Task<IReadOnlyList<TEvent>> NextAsync(int count, CancellationToken cancellationToken)
	{
		var events = new List<TEvent>(count);
		for (var i = 0; i < count; i++)
			events.Add(await NextAsync(cancellationToken).ConfigureAwait(false));
		return events;
	}

	public void Dispose() => _registration.Dispose();
}
