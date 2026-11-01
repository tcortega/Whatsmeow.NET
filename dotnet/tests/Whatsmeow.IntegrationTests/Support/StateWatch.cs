using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>A session's state watch, read by condition: the next state, or the states until an expected one.</summary>
internal sealed class StateWatch(IAsyncEnumerator<SessionState> states) : IAsyncDisposable
{
	public static StateWatch Start(IWhatsAppClient client, CancellationToken cancellationToken) =>
		new(client.WatchStateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));

	/// <summary>The next state; the watch must not end.</summary>
	public async Task<SessionState> NextAsync()
	{
		Assert.True(await states.MoveNextAsync().ConfigureAwait(false), "the watch ended");
		return states.Current;
	}

	/// <summary>
	/// Reads until <paramref name="expected"/>. A watch may skip states, so the ones in <paramref name="passing"/> may
	/// come first; any other state fails.
	/// </summary>
	public async Task ReachesAsync(SessionState expected, params SessionState[] passing)
	{
		for (var state = await NextAsync().ConfigureAwait(false); state != expected; state = await NextAsync().ConfigureAwait(false))
			Assert.Contains(state, passing);
	}

	/// <summary>The watch ends with <typeparamref name="TException"/>.</summary>
	public async Task<TException> EndsWithAsync<TException>()
		where TException : Exception =>
		await Assert.ThrowsAsync<TException>(async () => await states.MoveNextAsync().ConfigureAwait(false)).ConfigureAwait(false);

	public ValueTask DisposeAsync() => states.DisposeAsync();
}
