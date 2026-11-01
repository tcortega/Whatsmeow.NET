using Grpc.Core;
using Whatsmeow.IntegrationTests.Fake;

namespace Whatsmeow.IntegrationTests;

/// <summary>The shared fixture's fake WhatsApp control, reached through the sidecar's own channel.</summary>
public sealed class FakeSidecarTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Fake_TheControlIsServedOnTheSidecarsChannel()
	{
		var ct = TestContext.Current.CancellationToken;

		// No slice creates a client yet, so the fake refuses the delivery: that answer can only come from the fake.
		var error = await Assert.ThrowsAsync<RpcException>(() =>
			fixture.Fake.DeliverMessageAsync(new DeliverMessageRequest { SessionId = "fake-control", Id = "M1" }, cancellationToken: ct).ResponseAsync
		);

		Assert.Equal((StatusCode.Internal, """deliver message "M1" to session "fake-control": the session has no client"""), (error.StatusCode, error.Status.Detail));
	}
}

/// <summary>A crash of the shared sidecar leaves the fixture working for the next test.</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class FakeSidecarCrashTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Crash_TheNextTestGetsARestartedSidecar()
	{
		var ct = TestContext.Current.CancellationToken;

		await fixture.CrashAsync(ct);

		Assert.Equal(binaries.Expected, await fixture.Sidecar.GetInfoAsync(ct));
		var error = await Assert.ThrowsAsync<RpcException>(() =>
			fixture.Fake.DeliverMessageAsync(new DeliverMessageRequest { SessionId = "crash", Id = "M1" }, cancellationToken: ct).ResponseAsync
		);
		Assert.Equal((StatusCode.Internal, """deliver message "M1" to session "crash": the session has no client"""), (error.StatusCode, error.Status.Detail));
	}
}
