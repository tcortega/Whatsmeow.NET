namespace Whatsmeow.LiveTests;

/// <summary>Real time, for the live tests only: they measure WhatsApp and bound their waits on it; nothing there can be faked.</summary>
internal static class LiveClock
{
#pragma warning disable RS0030 // Justification: a live test measures and bounds real time against real WhatsApp.
	public static TimeProvider Time { get; } = TimeProvider.System;
#pragma warning restore RS0030

	/// <summary>A token that ends after <paramref name="bound"/> or with <paramref name="cancellationToken"/>.</summary>
	public static BoundToken Bound(TimeSpan bound, CancellationToken cancellationToken) => new(bound, cancellationToken);
}

/// <summary>A token that ends after a bound on <see cref="LiveClock.Time"/> or with another token.</summary>
internal sealed class BoundToken : IDisposable
{
	private readonly CancellationTokenSource _timer;
	private readonly CancellationTokenSource _linked;

	public BoundToken(TimeSpan bound, CancellationToken cancellationToken)
	{
		_timer = new CancellationTokenSource(bound, LiveClock.Time);
		_linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timer.Token);
	}

	public CancellationToken Token => _linked.Token;

	public void Dispose()
	{
		_linked.Dispose();
		_timer.Dispose();
	}
}
