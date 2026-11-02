using System.Threading.Channels;
using Whatsmeow.Events;

namespace Whatsmeow.LiveTests;

/// <summary>The events a client's handler got that match a filter, in order, read with a bound: real WhatsApp may send fewer.</summary>
internal sealed class LiveEvents<TEvent> : IDisposable
	where TEvent : WhatsAppEvent
{
	private readonly Channel<TEvent> _seen = Channel.CreateUnbounded<TEvent>();
	private readonly IDisposable _registration;

	public LiveEvents(IWhatsAppClient client, Func<TEvent, bool> filter) =>
		_registration = client.On<TEvent>(
			(e, token) =>
			{
				if (filter(e))
					_ = _seen.Writer.TryWrite(e);
				return ValueTask.CompletedTask;
			}
		);

	/// <summary>Up to <paramref name="count"/> more, as many as came within <paramref name="bound"/>.</summary>
	public async Task<IReadOnlyList<TEvent>> NextAsync(int count, TimeSpan bound, CancellationToken cancellationToken)
	{
		var seen = new List<TEvent>();
		using var bounded = LiveClock.Bound(bound, cancellationToken);
		try
		{
			while (seen.Count < count)
				seen.Add(await _seen.Reader.ReadAsync(bounded.Token).ConfigureAwait(false));
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

		return seen;
	}

	public void Dispose() => _registration.Dispose();
}
