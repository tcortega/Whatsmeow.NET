using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using static Whatsmeow.Testing.Tests.Samples;

namespace Whatsmeow.Testing.Tests;

/// <summary>
/// A host built with <c>AddWhatsmeow</c> whose session a test replaced with a fake: the application's handlers get the
/// fake's events, and no sidecar starts (ADR 0036). The sidecar path names a file that does not exist, so a host that
/// tried to start one would fail to start.
/// </summary>
public sealed class HostingTests : IDisposable
{
	private const string Sales = "sales";

	private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("wmn-fake-host-");

	public void Dispose() => _directory.Delete(recursive: true);

	[Fact(Timeout = 30_000)]
	public async Task ReplacedSession_TheApplicationsHandlersGetTheFakesEvents_AndNoSidecarStarts()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var fake = new FakeWhatsAppClient(SessionId.From(Sales));
		var builder = Application();
		_ = builder.Services.AddWhatsmeow().UseFakeClient(fake);
		using var host = builder.Build();

		await host.StartAsync(ct);
		await fake.RaiseAsync(Message(Ana, "ping", fake.SessionId), ct);

		Assert.Equal([fake], host.Services.GetKeyedServices<IWhatsAppClient>(Sales));
		var reply = Assert.Single(fake.Calls);
		Assert.Equal(nameof(IWhatsAppClient.SendTextAsync), reply.Member);
		Assert.Equal((Ana, "echo: ping"), (reply.Arguments["chat"], reply.Arguments["text"]));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = 30_000)]
	public async Task FakeReplacedBeforeTheApplicationRegistered_StillReplacesTheSession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var fake = new FakeWhatsAppClient(SessionId.From(Sales));
		var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _directory.FullName });
		builder.Configuration["Whatsmeow:SidecarPath"] = MissingSidecar();
		_ = builder.Services.AddWhatsmeow().UseFakeClient(fake);
		Register(builder);
		using var host = builder.Build();

		await host.StartAsync(ct);
		await fake.RaiseAsync(Message(Ana, "ping", fake.SessionId), ct);

		Assert.Same(fake, host.Services.GetRequiredKeyedService<IWhatsAppClient>(Sales));
		Assert.Equal("echo: ping", Assert.Single(fake.Calls).Arguments["text"]);
		await host.StopAsync(ct);
	}

	[Fact(Timeout = 30_000)]
	public async Task DeclaredSessionWithoutAFake_FailsTheHostStart()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var fake = new FakeWhatsAppClient(SessionId.From(Sales));
		var builder = Application();
		_ = builder.Services.AddWhatsmeow().AddSession("support").UseFakeClient(fake);
		using var host = builder.Build();

		var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(ct));

		Assert.Contains("support", failed.Message, StringComparison.Ordinal);
	}

	[Fact(Timeout = 30_000)]
	public async Task HostStopped_ItsHandlersComeOffTheFake_WhichTheTestStillOwns()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var fake = new FakeWhatsAppClient(SessionId.From(Sales));
		var builder = Application();
		_ = builder.Services.AddWhatsmeow().UseFakeClient(fake);
		using (var host = builder.Build())
		{
			await host.StartAsync(ct);
			await host.StopAsync(ct);
		}

		var errors = new List<Exception>();
		using var onError = fake.OnHandlerError(error => errors.Add(error.Exception));
		await fake.RaiseAsync(Message(Ana, "after the host", fake.SessionId), ct);

		// No handler ran: one that did would fail on the disposed container, and answer nothing.
		Assert.Empty(errors);
		Assert.Empty(fake.Calls);
	}

	/// <summary>The application's own registrations: one declared session and a handler that answers every message.</summary>
	private HostApplicationBuilder Application()
	{
		var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _directory.FullName });
		builder.Configuration["Whatsmeow:SidecarPath"] = MissingSidecar();
		Register(builder);
		return builder;
	}

	private static void Register(HostApplicationBuilder builder) => _ = builder.Services.AddWhatsmeow().AddSession(Sales).AddHandler<MessageReceived, EchoHandler>();

	private string MissingSidecar() => Path.Combine(_directory.FullName, "no-whatsmeow-net-here");

	/// <summary>The application's handler: answers a text with "echo: " and the text, on the event's own client.</summary>
	internal sealed class EchoHandler : IWhatsAppEventHandler<MessageReceived>
	{
		public async ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken) =>
			_ = await client.SendTextAsync(e.Source.Chat, "echo: " + Text(e), cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
