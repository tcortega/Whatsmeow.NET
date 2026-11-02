using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Hosting;

/// <summary>
/// Whatsmeow.Extensions.Hosting in a generic host (ADR 0034). Each test's host starts a fake-mode sidecar of its own on
/// a database of its own, because the host starting and stopping the sidecar is the behaviour under test.
/// </summary>
public sealed class HostingTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task HostStop_WithAShutdownTimeoutBelowTheStopGrace_FlushesTheAcksAndStopsTheSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();

		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		// The clock never moves, so the ack timer never fires: only the stop can ack (ARCHITECTURE §8.4).
		_ = first.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
		_ = first.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
		_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-stop").AddHandler<MessageReceived, RecordingHandler>();
		using var firstHost = first.Build();
		await firstHost.StartAsync(ct);
		var (processId, _) = await logs.StartedAsync(1, ct);
		await Hosts.LinkAsync(firstHost, "hosting-stop", ct);
		await Hosts.Client(firstHost, "hosting-stop").ConnectAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(firstHost), "hosting-stop", ct, "M1", "M2");
		Assert.Equal(
			[Deliveries.Received("hosting-stop", "M1"), Deliveries.Received("hosting-stop", "M2")],
			(await Hosts.Handled(firstHost).NextAsync(2, ct)).Select(handling => handling.Event)
		);

		await firstHost.StopAsync(ct);
		Processes.AssertGone(processId);

		// The first host is stopped, not disposed: the second starts on its database only because the first sidecar is gone.
		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-stop").AddHandler<MessageReceived, RecordingHandler>();
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(secondHost), "hosting-stop", ct, "M3");

		Assert.Equal(Deliveries.Received("hosting-stop", "M3"), (await Hosts.Handled(secondHost).NextAsync(ct)).Event);
		await secondHost.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostStart_CreatesTheDeclaredSessions_ConnectsThePairedOnes_AndLeavesTheUnpairedOnes()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();
		var sessions = new[] { "hosting-paired", "hosting-unpaired" };

		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession(sessions[0]).AddSession(sessions[1]);
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			Assert.Equal(
				[new SessionInfo { Id = SessionId.From(sessions[0]), State = new SessionState.Created() }, new SessionInfo { Id = SessionId.From(sessions[1]), State = new SessionState.Created() }],
				await Hosts.Sidecar(firstHost).Sessions.ListAsync(ct)
			);
			await Hosts.LinkAsync(firstHost, sessions[0], ct);
			await firstHost.StopAsync(ct);
		}

		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession(sessions[0]).AddSession(sessions[1]);
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);

		var watch = StateWatch.Start(Hosts.Client(secondHost, sessions[0]), ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		Assert.Equal(new SessionState.Created(), (await Hosts.Sidecar(secondHost).Sessions.ListAsync(ct)).Single(session => session.Id == SessionId.From(sessions[1])).State);
		await secondHost.StopAsync(ct);
	}

	[Fact]
	public void Options_FromConfigurationAndTheEnvironment_GetTheSetupStepOfTheNoDiPath()
	{
		using var logs = new LogCapture();
		var media = binaries.NewDirectory();
		var builder = Hosts.Builder(
			logs,
			new(StringComparer.Ordinal) { ["Whatsmeow:SidecarPath"] = "configured-sidecar", ["Whatsmeow:DatabasePath"] = "hosting-options.db", ["Whatsmeow:MediaRoot"] = media + Path.DirectorySeparatorChar }
		);

		// A prefix of this test's own, so no other configuration in the process sees the variable.
		const string Prefix = "WMN_HOSTING_OPTIONS_TEST_";
		Environment.SetEnvironmentVariable(Prefix + "Whatsmeow__StartupTimeout", "00:00:45");
		try
		{
			_ = builder.Configuration.AddEnvironmentVariables(Prefix);
			_ = builder.Services.AddWhatsmeow();
			using var host = builder.Build();
			var options = host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value;

			// The setup step made the database a full path against the current directory and trimmed the media root.
			Assert.Equal(
				("configured-sidecar", Path.Combine(Directory.GetCurrentDirectory(), "hosting-options.db"), TimeSpan.FromSeconds(45), SidecarTransport.UnixDomainSocket, media),
				(options.SidecarPath, options.DatabasePath, options.StartupTimeout, options.Transport, options.MediaRoot)
			);
		}
		finally
		{
			Environment.SetEnvironmentVariable(Prefix + "Whatsmeow__StartupTimeout", value: null);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task InvalidOptions_TheHostStartFailsWithOptionsValidationException_AndNoSidecarStarts()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var settings = Hosts.Settings(fixture.Binary, binaries.NewDatabase());
		settings["Whatsmeow:StartupTimeout"] = "00:00:00";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp();
		using var host = builder.Build();

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(ct));

		Assert.Equal(["StartupTimeout must be more than zero and at most 01:00:00."], error.Failures, StringComparer.Ordinal);
		// The same rules are the options' validator, for any service that reads them.
		_ = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value);
		Assert.Empty(logs.Named("SidecarStarted"));
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarCannotStart_TheHostStartFailsWithSidecarUnavailable_OnTheTimeProviderOfTheContainer()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var stub = binaries.Stage(binaries.HangingStub);
		var settings = Hosts.Settings(stub, binaries.NewDatabase());
		settings["Whatsmeow:StartupTimeout"] = "01:00:00";
		var builder = Hosts.Builder(logs, settings);
		var time = new FakeTimeProvider();
		_ = builder.Services.AddSingleton<TimeProvider>(time);
		_ = builder.Services.AddWhatsmeow();
		using var host = builder.Build();

		var start = host.StartAsync(ct);
		// The stub writes one stderr line and never its ready line; the start's timeout runs from before the spawn.
		_ = await logs.WaitForAsync(record => record.Category == "Whatsmeow.Sidecar", ct);
		time.Advance(TimeSpan.FromHours(1));

		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => start);
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task KeyedClient_InjectedIntoAServiceThatStartsFirst_IsItsSessionsClientOnARunningSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddSingleton<Observed>();
		_ = builder.Services.AddHostedService<KeyedConsumer>();
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-keyed");
		using var host = builder.Build();

		await host.StartAsync(ct);

		var (client, state) = await host.Services.GetRequiredService<Observed>().Start.Task;
		Assert.Equal((SessionId.From("hosting-keyed"), new SessionState.Created()), (client.SessionId, state));
		Assert.Same(client, await Hosts.Sidecar(host).Sessions.GetOrCreateAsync(SessionId.From("hosting-keyed"), ct));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task ConnectFailsAtHostStart_TheHostStartFails_AndLeavesNoSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();

		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-failfast");
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			await Hosts.LinkAsync(firstHost, "hosting-failfast", ct);
			await firstHost.StopAsync(ct);
		}

		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-failfast");
		// The host resolves the keyed client once the sidecar runs, right before connecting it: the fake WhatsApp is told
		// then to refuse that connect. Blocking, since a factory is synchronous.
		_ = second.Services.AddKeyedSingleton<IWhatsAppClient>(
			"hosting-failfast",
			static (services, key) =>
			{
				var sidecar = services.GetRequiredService<WhatsmeowSidecar>();
				_ = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker).FailNextConnect(new FailNextConnectRequest { SessionId = "hosting-failfast" });
				return sidecar.Sessions.Client(SessionId.From((string)key!));
			}
		);
		using var secondHost = second.Build();

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => secondHost.StartAsync(ct));
		var (processId, _) = await logs.StartedAsync(2, ct);
		Processes.AssertGone(processId);
	}

	[Fact(Timeout = Timeout)]
	public async Task SessionCreatedAtRuntime_ThroughTheRegisteredSidecar_GetsTheHandlersOfEverySession()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);

		var client = await Hosts.Sidecar(host).Sessions.GetOrCreateAsync(SessionId.From("hosting-runtime"), ct);
		await Hosts.LinkAsync(host, "hosting-runtime", ct);
		await client.ConnectAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-runtime", ct, "M1");

		Assert.Equal(new Handling { Handler = "every", Event = Deliveries.Received("hosting-runtime", "M1"), Client = client }, await Hosts.Handled(host).NextAsync(ct));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_EachEventInAScopeOfItsOwn_GetsAFreshScopedDependency()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddScoped<ScopedProbe>().AddSingleton<Probes>();
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-scope").AddHandler<MessageReceived, ProbingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		await Hosts.LinkAsync(host, "hosting-scope", ct);
		await Hosts.Client(host, "hosting-scope").ConnectAsync(ct);

		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-scope", ct, "M1", "M2");
		var probes = host.Services.GetRequiredService<Probes>();
		var first = await probes.NextAsync(ct);
		var second = await probes.NextAsync(ct);

		// M2's handler runs after M1's returned, in the same chat: M1's scope has ended by then.
		Assert.NotSame(first, second);
		Assert.True(first.Disposed, "the scope of M1's handler was not disposed");
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_Throws_OnHandlerErrorIsRaised_AndTheNextHandlerAndEventStillRun()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-throws").AddHandler<MessageReceived, FailingHandler>().AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var client = Hosts.Client(host, "hosting-throws");
		var errors = new TaskCompletionSource<HandlerError>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var onError = client.OnHandlerError(error => errors.TrySetResult(error));
		await Hosts.LinkAsync(host, "hosting-throws", ct);
		await client.ConnectAsync(ct);

		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-throws", ct, "M1", "M2");

		Assert.Equal(new HandlerError { Event = Deliveries.Received("hosting-throws", "M1"), Exception = FailingHandler.Failure }, await errors.Task.WaitAsync(ct));
		Assert.Equal(
			[Deliveries.Received("hosting-throws", "M1"), Deliveries.Received("hosting-throws", "M2")],
			(await Hosts.Handled(host).NextAsync(2, ct)).Select(handling => handling.Event)
		);
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HandlerForOneSession_GetsOnlyThatSessionsEvents()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder
			.Services.AddWhatsmeow()
			.UseFakeWhatsApp()
			.AddSession("hosting-only")
			.AddSession("hosting-other")
			.AddHandler<MessageReceived, SessionHandler>("hosting-only")
			.AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var only = Hosts.Client(host, "hosting-only");
		var other = Hosts.Client(host, "hosting-other");
		await Hosts.LinkAsync(host, "hosting-only", ct);
		await Hosts.LinkAsync(host, "hosting-other", ct);
		await only.ConnectAsync(ct);
		await other.ConnectAsync(ct);
		var handled = Hosts.Handled(host);

		// An event's handlers run in the order they were registered, so "only" would come before "every".
		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-other", ct, "M1");
		var first = await handled.NextAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-only", ct, "M2");

		Assert.Equal(
			[
				new Handling { Handler = "every", Event = Deliveries.Received("hosting-other", "M1"), Client = other },
				new Handling { Handler = "only", Event = Deliveries.Received("hosting-only", "M2"), Client = only },
				new Handling { Handler = "every", Event = Deliveries.Received("hosting-only", "M2"), Client = only },
			],
			[first, .. await handled.NextAsync(2, ct)]
		);
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarCrash_RestartsWithTheBackoff_AndTheHostKeepsRunning()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-crash");
		using var host = builder.Build();
		await host.StartAsync(ct);
		var (processId, _) = await logs.StartedAsync(1, ct);

		using (var process = Process.GetProcessById(processId))
			process.Kill();
		var restarted = await logs.WaitForAsync(record => record.Id.Name == "SidecarRestarted", ct);

		// The first restart of the #4 backoff (ADR 0017), and the host never heard of the crash.
		Assert.Equal(("00:00:01", "1"), (restarted.GetStructuredStateValue("Delay"), restarted.GetStructuredStateValue("Attempt")));
		Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
		Assert.Equal([new SessionInfo { Id = SessionId.From("hosting-crash"), State = new SessionState.Created() }], await Hosts.Sidecar(host).Sessions.ListAsync(ct));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task AddWhatsmeowTwice_StartsOneSidecar_WithTheSessionsOfBoth()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-twice-a");
		_ = builder.Services.AddWhatsmeow().AddSession("hosting-twice-b").AddSession("hosting-twice-a");
		using var host = builder.Build();

		await host.StartAsync(ct);

		_ = Assert.Single(logs.Named("SidecarStarted"));
		Assert.Equal(
			[new SessionInfo { Id = SessionId.From("hosting-twice-a"), State = new SessionState.Created() }, new SessionInfo { Id = SessionId.From("hosting-twice-b"), State = new SessionState.Created() }],
			await Hosts.Sidecar(host).Sessions.ListAsync(ct)
		);
		await host.StopAsync(ct);
	}
}
