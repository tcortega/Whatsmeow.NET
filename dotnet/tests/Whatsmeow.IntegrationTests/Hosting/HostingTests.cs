using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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
	public void Options_FromConfigurationAndTheEnvironment_GetTheSetupStepWithRelativePathsUnderTheContentRoot()
	{
		using var logs = new LogCapture();
		var contentRoot = binaries.NewDirectory();
		var media = Directory.CreateDirectory(Path.Combine(contentRoot, "media")).FullName;
		var builder = Hosts.Builder(
			logs,
			new(StringComparer.Ordinal) { ["Whatsmeow:SidecarPath"] = "configured-sidecar", ["Whatsmeow:DatabasePath"] = "hosting-options.db", ["Whatsmeow:MediaRoot"] = "media" + Path.DirectorySeparatorChar },
			contentRoot
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

			// Relative paths are the content root's, not the current directory's (ADR 0034); the setup step trimmed the media root.
			Assert.Equal(
				("configured-sidecar", Path.Combine(contentRoot, "hosting-options.db"), TimeSpan.FromSeconds(45), SidecarTransport.UnixDomainSocket, media),
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

		Assert.Equal(["StartupTimeout 00:00:00 must be more than zero and at most 01:00:00."], error.Failures, StringComparer.Ordinal);
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
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-keyed").AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();

		await host.StartAsync(ct);

		var (client, state) = await host.Services.GetRequiredService<Observed>().Start.Task;
		Assert.Equal((SessionId.From("hosting-keyed"), new SessionState.Created()), (client.SessionId, state));
		Assert.Same(client, await Hosts.Sidecar(host).Sessions.GetOrCreateAsync(SessionId.From("hosting-keyed"), ct));

		// The client handed out before the start is the one the handlers are on.
		await Hosts.LinkAsync(host, "hosting-keyed", ct);
		await client.ConnectAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-keyed", ct, "M1");
		Assert.Equal(new Handling { Handler = "every", Event = Deliveries.Received("hosting-keyed", "M1"), Client = client }, await Hosts.Handled(host).NextAsync(ct));
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
		// Its StartAsync runs once the sidecar runs and before the declared sessions connect, in StartedAsync.
		_ = second.Services.AddHostedService<RefuseNextConnect>();
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

	[Fact]
	public void Options_WithoutADatabasePath_TheDefaultFileIsInTheContentRoot()
	{
		using var logs = new LogCapture();
		var contentRoot = binaries.NewDirectory();
		var builder = Hosts.Builder(logs, new(StringComparer.Ordinal), contentRoot);
		_ = builder.Services.AddWhatsmeow();
		using var host = builder.Build();

		Assert.Equal(Path.Combine(contentRoot, "whatsmeow-net.db"), host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value.DatabasePath);
	}

	[Fact]
	public void HistoryOptions_FromConfiguration_AreBound()
	{
		using var logs = new LogCapture();
		var builder = Hosts.Builder(
			logs,
			new(StringComparer.Ordinal)
			{
				["Whatsmeow:History:RequestFullSync"] = "true",
				["Whatsmeow:History:FullSyncDaysLimit"] = "30",
				["Whatsmeow:History:FullSyncSizeLimitMegabytes"] = "100",
				["Whatsmeow:History:StorageQuotaMegabytes"] = "512",
			},
			binaries.NewDirectory()
		);
		_ = builder.Services.AddWhatsmeow();
		using var host = builder.Build();

		var history = host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value.History;

		Assert.Equal((true, 30, 100, 512), (history.RequestFullSync, history.FullSyncDaysLimit, history.FullSyncSizeLimitMegabytes, history.StorageQuotaMegabytes));
	}

	[Fact(Timeout = Timeout)]
	public async Task HistoryOptions_FromConfiguration_ALimitOfZeroFailsValidation_AndNoSidecarStarts()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var settings = Hosts.Settings(fixture.Binary, binaries.NewDatabase());
		settings["Whatsmeow:History:FullSyncDaysLimit"] = "0";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp();
		using var host = builder.Build();

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(ct));

		Assert.Equal(["History.FullSyncDaysLimit must be null or more than zero."], error.Failures, StringComparer.Ordinal);
		Assert.Empty(logs.Named("SidecarStarted"));
	}

	[Fact]
	public void SendPacing_FromConfiguration_IsBound()
	{
		using var logs = new LogCapture();
		var builder = Hosts.Builder(
			logs,
			new(StringComparer.Ordinal) { ["Whatsmeow:SendPacing:Burst"] = "3", ["Whatsmeow:SendPacing:Interval"] = "00:00:02.5" },
			binaries.NewDirectory()
		);
		_ = builder.Services.AddWhatsmeow();
		using var host = builder.Build();

		var pacing = host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value.SendPacing;

		Assert.Equal((3, TimeSpan.FromSeconds(2.5)), (pacing.Burst, pacing.Interval));
	}

	[Fact(Timeout = Timeout)]
	public async Task SendPacing_FromConfiguration_ABurstOfZeroFailsValidation_AndNoSidecarStarts()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var settings = Hosts.Settings(fixture.Binary, binaries.NewDatabase());
		settings["Whatsmeow:SendPacing:Burst"] = "0";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp();
		using var host = builder.Build();

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(ct));

		Assert.Equal(["SendPacing.Burst 0 must be 1 to 1000."], error.Failures, StringComparer.Ordinal);
		Assert.Empty(logs.Named("SidecarStarted"));
	}

	[Fact(Timeout = Timeout)]
	public async Task Transport_FromConfiguration_ANumberNoTransportHasFailsValidation()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var settings = Hosts.Settings(fixture.Binary, binaries.NewDatabase());
		settings["Whatsmeow:Transport"] = "7";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp();
		using var host = builder.Build();

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(ct));

		Assert.Equal(["Transport 7 is not a transport."], error.Failures, StringComparer.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task UnparsableOption_TheHostStartFailsWithTheBindersInvalidOperationException()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var settings = Hosts.Settings(fixture.Binary, binaries.NewDatabase());
		settings["Whatsmeow:Transport"] = "Pigeon";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp();
		using var host = builder.Build();

		// The binder fails before any rule runs, so this is not an OptionsValidationException (ADR 0034).
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(ct));

		Assert.Contains("'Pigeon'", error.Message, StringComparison.Ordinal);
		Assert.Empty(logs.Named("SidecarStarted"));
	}

	[Fact(Timeout = Timeout)]
	public async Task AddHandlerTwice_TheHandlerRunsOncePerEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-handler-twice").AddHandler<MessageReceived, RecordingHandler>();
		_ = builder.Services.AddWhatsmeow().AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var client = Hosts.Client(host, "hosting-handler-twice");
		await Hosts.LinkAsync(host, "hosting-handler-twice", ct);
		await client.ConnectAsync(ct);

		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-handler-twice", ct, "M1", "M2");

		// A second run of M1's handler would come before M2's.
		Assert.Equal(
			[Deliveries.Received("hosting-handler-twice", "M1"), Deliveries.Received("hosting-handler-twice", "M2")],
			(await Hosts.Handled(host).NextAsync(2, ct)).Select(handling => handling.Event)
		);
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HandlerOfEveryEvent_RegisteredForMessages_GetsTheMessagesOnly()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-contravariant").AddHandler<MessageReceived, AnyEventHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var client = Hosts.Client(host, "hosting-contravariant");
		var errors = new TaskCompletionSource<HandlerError>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var onError = client.OnHandlerError(error => errors.TrySetResult(error));
		await Hosts.LinkAsync(host, "hosting-contravariant", ct);
		await client.ConnectAsync(ct);

		await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-contravariant", ct, "M1");

		// The connect's state changes came first; a handler that got one would have failed.
		Assert.Equal(new Handling { Handler = "any", Event = Deliveries.Received("hosting-contravariant", "M1"), Client = client }, await Hosts.Handled(host).NextAsync(ct));
		Assert.False(errors.Task.IsCompleted, "a handler registered for messages got another event");
		await host.StopAsync(ct);
	}

	[Fact]
	public void Builder_Services_IsTheCollectionAddWhatsmeowExtends()
	{
		var services = new ServiceCollection();

		Assert.Same(services, services.AddWhatsmeow().Services);
	}

	[Fact(Timeout = Timeout)]
	public async Task LaterServiceFailsToStart_NoEventIsHandled_AndThePendingOnesComeInTheNextHost()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();

		// M1 is pending: its handler was still running when the first host stopped, and the clock never acked it.
		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = first.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
		_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-later").AddHandler<MessageReceived, HoldingHandler>();
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			await Hosts.LinkAsync(firstHost, "hosting-later", ct);
			await Hosts.Client(firstHost, "hosting-later").ConnectAsync(ct);
			await Deliveries.DeliverAsync(Hosts.Fake(firstHost), "hosting-later", ct, "M1");
			_ = await Hosts.Handled(firstHost).NextAsync(ct);
			await firstHost.StopAsync(ct);
		}

		// A service after AddWhatsmeow fails, so the host never stops ours: it only disposes the container. The session
		// was not connected yet when that service started, so no event flowed into handlers the container is disposing.
		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddSingleton<Observed>();
		_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-later").AddHandler<MessageReceived, RecordingHandler>();
		_ = second.Services.AddHostedService<FailingStart>();
		using (var secondHost = second.Build())
		{
			Assert.Same(FailingStart.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => secondHost.StartAsync(ct)));
			Assert.Equal(new SessionState.Disconnected(), (await secondHost.Services.GetRequiredService<Observed>().Start.Task).State);
		}

		var third = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = third.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-later").AddHandler<MessageReceived, RecordingHandler>();
		using var thirdHost = third.Build();
		await thirdHost.StartAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(thirdHost), "hosting-later", ct, "M2");

		Assert.Equal(
			[Deliveries.Received("hosting-later", "M1"), Deliveries.Received("hosting-later", "M2")],
			(await Hosts.Handled(thirdHost).NextAsync(2, ct)).Select(handling => handling.Event)
		);
		await thirdHost.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task DeleteAsync_OfADeclaredSession_ThrowsAndKeepsTheSession()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-delete");
		using var host = builder.Build();
		await host.StartAsync(ct);
		var sessions = Hosts.Sidecar(host).Sessions;

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.DeleteAsync(SessionId.From("hosting-delete"), ct));

		Assert.Equal("Session hosting-delete is declared with AddSession, so it cannot be deleted while the host runs: its keyed client would stay behind.", error.Message);
		Assert.Equal([new SessionInfo { Id = SessionId.From("hosting-delete"), State = new SessionState.Created() }], await sessions.ListAsync(ct));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task KeyedRegistrationReplaced_TheHostConnectsTheClientTheHandlersAreOn()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();

		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-own");
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			await Hosts.LinkAsync(firstHost, "hosting-own", ct);
			await firstHost.StopAsync(ct);
		}

		// The application replaces the keyed registration; the declared session is still the sidecar's own client.
		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-own").AddHandler<MessageReceived, RecordingHandler>();
		_ = second.Services.AddKeyedSingleton<IWhatsAppClient>(
			"hosting-own",
			static (services, _) => services.GetRequiredService<WhatsmeowSidecar>().Sessions.Client(SessionId.From("hosting-decoy"))
		);
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(secondHost), "hosting-own", ct, "M1");

		var own = await Hosts.Sidecar(secondHost).Sessions.GetOrCreateAsync(SessionId.From("hosting-own"), ct);
		Assert.Equal(new Handling { Handler = "every", Event = Deliveries.Received("hosting-own", "M1"), Client = own }, await Hosts.Handled(secondHost).NextAsync(ct));
		await secondHost.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task DisposeDuringTheHostStart_LeavesNoProcessAndNoSocketDirectory()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var stub = binaries.Stage(binaries.HangingStub);
		var settings = Hosts.Settings(stub, binaries.NewDatabase());
		settings["Whatsmeow:StartupTimeout"] = "01:00:00";
		var builder = Hosts.Builder(logs, settings);
		_ = builder.Services.AddWhatsmeow();
		using var host = builder.Build();

		var start = host.StartAsync(ct);
		// The stub writes one stderr line and never its ready line, so the start hangs until the dispose.
		_ = await logs.WaitForAsync(record => record.Category == "Whatsmeow.Sidecar", ct);
		var socketDirectory = Processes.StubArgument(stub, "--socket-dir");
		await Hosts.Sidecar(host).DisposeAsync();

		// The dispose cancelled the start and returned only once it had ended: nothing of it is left, nor runs on.
		Processes.AssertGone(Processes.StubProcessId(stub));
		Assert.False(Directory.Exists(socketDirectory), $"the socket directory {socketDirectory} is still there");
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostStop_AHandlerIgnoringItsToken_TheSidecarStillStops_AndTheEventComesAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();
		var gate = new Gate();
		try
		{
			var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
			_ = first.Services.AddSingleton(gate);
			_ = first.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);
			_ = first.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-stuck").AddHandler<MessageReceived, StuckHandler>();
			using (var firstHost = first.Build())
			{
				await firstHost.StartAsync(ct);
				var (processId, _) = await logs.StartedAsync(1, ct);
				await Hosts.LinkAsync(firstHost, "hosting-stuck", ct);
				await Hosts.Client(firstHost, "hosting-stuck").ConnectAsync(ct);
				await Deliveries.DeliverAsync(Hosts.Fake(firstHost), "hosting-stuck", ct, "M1");
				_ = await Hosts.Handled(firstHost).NextAsync(ct);

				await firstHost.StopAsync(ct);
				Processes.AssertGone(processId);
			}

			// M1's handler never finished, so M1 was never acked.
			var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
			_ = second.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-stuck").AddHandler<MessageReceived, RecordingHandler>();
			using var secondHost = second.Build();
			await secondHost.StartAsync(ct);

			Assert.Equal(Deliveries.Received("hosting-stuck", "M1"), (await Hosts.Handled(secondHost).NextAsync(ct)).Event);
			await secondHost.StopAsync(ct);
		}
		finally
		{
			gate.Open.SetResult();
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task HostWithAMeterFactory_TheSidecarsMeterComesFromIt()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddSession("hosting-meter");
		using var host = builder.Build();
		// Every host builder registers one; a collector on it sees this host's measurements only.
		using var errors = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		await host.StartAsync(ct);

		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => Hosts.Client(host, "hosting-meter").SendTextAsync(Jid.Parse("900000403@lid"), "hi", cancellationToken: ct));

		var measurement = Assert.Single(errors.GetMeasurementSnapshot());
		Assert.Equal(1L, measurement.Value);
		Assert.Equal(Tags.Of(("error.type", "NotLoggedIn")), Tags.Of(measurement));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostStop_ExternalMode_LeavesTheSidecarAndItsSessionsRunning()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var contentRoot = binaries.NewDirectory();
		var token = StandaloneSidecar.NewTokenFile(contentRoot);
		await using var standalone = await StandaloneSidecar.StartAsync(fixture.Binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, ["--fake-whatsapp"], ct);

		// From configuration, as a container's appsettings or environment would give it; the token file in the content root.
		var builder = Hosts.Builder(
			logs,
			new(StringComparer.Ordinal) { ["Whatsmeow:External:Address"] = standalone.Address, ["Whatsmeow:External:TokenPath"] = Path.GetFileName(token), ["Whatsmeow:External:Consumer"] = "hosting" },
			contentRoot
		);
		_ = builder.Services.AddWhatsmeow().AddSession("hosting-external").AddHandler<MessageReceived, RecordingHandler>();
		using (var host = builder.Build())
		{
			await host.StartAsync(ct);
			await Hosts.LinkAsync(host, "hosting-external", ct);
			await Hosts.Client(host, "hosting-external").ConnectAsync(ct);
			await Deliveries.DeliverAsync(Hosts.Fake(host), "hosting-external", ct, "M1");
			Assert.Equal(Deliveries.Received("hosting-external", "M1"), (await Hosts.Handled(host).NextAsync(ct)).Event);

			await host.StopAsync(ct);
		}

		// The host stopped what it started, its client, and never the sidecar an orchestrator runs.
		await using var after = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { External = new ExternalSidecarOptions { Address = standalone.Address, TokenPath = token } }, ct);
		var session = await after.Sessions.GetOrCreateAsync(SessionId.From("hosting-external"), ct);
		Assert.Equal(new SessionState.Connected(), await Sessions.SessionSetup.StateAsync(session, ct));
		Assert.False(standalone.HasExited, standalone.Stderr);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostRestart_TheExternalSidecarKeptRunning_TheNewHostsHandlersGetEvents()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var contentRoot = binaries.NewDirectory();
		var token = StandaloneSidecar.NewTokenFile(contentRoot);
		await using var standalone = await StandaloneSidecar.StartAsync(fixture.Binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, ["--fake-whatsapp"], ct);
		var settings = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Whatsmeow:External:Address"] = standalone.Address, ["Whatsmeow:External:TokenPath"] = token };

		var first = Hosts.Builder(logs, settings, contentRoot);
		_ = first.Services.AddWhatsmeow().AddSession("hosting-external-restart");
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			await Hosts.LinkAsync(firstHost, "hosting-external-restart", ct);
			await Hosts.Client(firstHost, "hosting-external-restart").ConnectAsync(ct);
			await firstHost.StopAsync(ct);
		}

		// The application restarts; the sidecar, and the session in it, stayed connected. The new host's handlers get its
		// events all the same: its start connects the declared session, which for a connected one only starts its events.
		var second = Hosts.Builder(logs, settings, contentRoot);
		_ = second.Services.AddWhatsmeow().AddSession("hosting-external-restart").AddHandler<MessageReceived, RecordingHandler>();
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);
		await Deliveries.DeliverAsync(Hosts.Fake(secondHost), "hosting-external-restart", ct, "M1");

		Assert.Equal(Deliveries.Received("hosting-external-restart", "M1"), (await Hosts.Handled(secondHost).NextAsync(ct)).Event);
		await secondHost.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostRestart_TheExternalSidecarIsReconnectingADeclaredSession_TheNewHostsHandlersGetItsEvents()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "hosting-external-reconnecting";
		using var logs = new LogCapture();
		var contentRoot = binaries.NewDirectory();
		var token = StandaloneSidecar.NewTokenFile(contentRoot);
		await using var standalone = await StandaloneSidecar.StartAsync(fixture.Binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, ["--fake-whatsapp"], ct);
		var settings = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Whatsmeow:External:Address"] = standalone.Address, ["Whatsmeow:External:TokenPath"] = token };

		var first = Hosts.Builder(logs, settings, contentRoot);
		_ = first.Services.AddWhatsmeow().AddSession(Session);
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			await Hosts.LinkAsync(firstHost, Session, ct);
			var client = Hosts.Client(firstHost, Session);
			await using (var watch = StateWatch.Start(client, ct))
			{
				await client.ConnectAsync(ct);
				await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting());

				// The sidecar resets the connection after an answer it withheld (ADR 0044), and the reconnect's login is held,
				// as one waiting on the network: the session stays reconnecting.
				_ = await Hosts.Fake(firstHost).HoldNextLoginAsync(new HoldNextLoginRequest { SessionId = Session }, cancellationToken: ct);
				var withheld = Deliveries.Message(Session, "M1");
				withheld.WithholdFirstAnswer = true;
				_ = await Hosts.Fake(firstHost).DeliverMessageAsync(withheld, cancellationToken: ct);
				await watch.ReachesAsync(new SessionState.Reconnecting(), new SessionState.CaughtUp());
			}

			await firstHost.StopAsync(ct);
		}

		// The application restarts while the sidecar reconnects the session, which is live for it (session.proto): the
		// new host's start connects it, which only starts this host's events, and its handlers get the message WhatsApp
		// delivers again at the login.
		var second = Hosts.Builder(logs, settings, contentRoot);
		_ = second.Services.AddWhatsmeow().AddSession(Session).AddHandler<MessageReceived, RecordingHandler>();
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);
		_ = await Hosts.Fake(secondHost).ReleaseLoginAsync(new ReleaseLoginRequest { SessionId = Session }, cancellationToken: ct);

		Assert.Equal(Deliveries.Received(Session, "M1"), (await Hosts.Handled(secondHost).NextAsync(ct)).Event);
		await secondHost.StopAsync(ct);
	}
}
