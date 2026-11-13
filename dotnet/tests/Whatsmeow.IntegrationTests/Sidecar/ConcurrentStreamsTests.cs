using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// The channel's one HTTP/2 connection carries every long-lived stream of every session (ARCHITECTURE §8.6): no
/// concurrent stream limit holds back a call while more streams than HTTP/2's customary 100 are open.
/// </summary>
public sealed class ConcurrentStreamsTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>Well past 100, the limit a server commonly advertises, and 250, Go's net/http2 default.</summary>
	private const int Streams = 300;

	[Fact(Timeout = Timeout)]
	public async Task ManyOpenStreams_ACallStillAnswers()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("concurrent-streams"), ct);

		var watches = new List<StateWatch>(Streams);
		try
		{
			// Each watch is a server stream that stays open; its first state proves it reached the sidecar.
			for (var i = 0; i < Streams; i++)
				watches.Add(StateWatch.Start(client, ct));
			var first = await Task.WhenAll(watches.Select(watch => watch.NextAsync()));
			Assert.All(first, state => Assert.Equal(new SessionState.Created(), state));

			// With every stream still open, a unary call is not queued behind them.
			_ = await fixture.Sidecar.GetInfoAsync(ct);
		}
		finally
		{
			foreach (var watch in watches)
				await watch.DisposeAsync();
		}
	}
}
