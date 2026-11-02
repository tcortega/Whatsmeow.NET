using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Hosting;

/// <summary>The generic hosts the Hosting tests build: no defaults, the Whatsmeow section from the test, the test's logs.</summary>
internal static class Hosts
{
	public static HostApplicationBuilder Builder(LogCapture logs, Dictionary<string, string?> settings)
	{
		var builder = Host.CreateEmptyApplicationBuilder(settings: null);
		_ = builder.Configuration.AddInMemoryCollection(settings);
		_ = builder.Services.AddSingleton(logs.Factory);
		_ = builder.Services.AddSingleton<Handled>();
		return builder;
	}

	/// <summary>The fake-mode sidecar on a database of its own.</summary>
	public static Dictionary<string, string?> Settings(string binary, string database) =>
		new(StringComparer.Ordinal) { ["Whatsmeow:SidecarPath"] = binary, ["Whatsmeow:DatabasePath"] = database };

	public static WhatsmeowSidecar Sidecar(IHost host) => host.Services.GetRequiredService<WhatsmeowSidecar>();

	public static IWhatsAppClient Client(IHost host, string session) => host.Services.GetRequiredKeyedService<IWhatsAppClient>(session);

	public static FakeWhatsAppService.FakeWhatsAppServiceClient Fake(IHost host) => new(Sidecar(host).CallInvoker);

	public static Handled Handled(IHost host) => host.Services.GetRequiredService<Handled>();

	/// <summary>Pairs <paramref name="session"/> the quick way, with the fake's LinkDevice (docs/guidelines/tests.md).</summary>
	public static async Task LinkAsync(IHost host, string session, CancellationToken cancellationToken) =>
		_ = await Fake(host).LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session) }, cancellationToken: cancellationToken).ConfigureAwait(false);
}

/// <summary>What a handler got: which handler, the event, and the client it was given.</summary>
internal sealed record Handling
{
	public required string Handler { get; init; }

	public required MessageReceived Event { get; init; }

	public required IWhatsAppClient Client { get; init; }
}

/// <summary>Every <see cref="Handling"/> of the host's handlers, read in order.</summary>
internal sealed class Handled
{
	private readonly Channel<Handling> _handled = Channel.CreateUnbounded<Handling>();

	public void Add(string handler, MessageReceived e, IWhatsAppClient client) =>
		_ = _handled.Writer.TryWrite(new Handling { Handler = handler, Event = e, Client = client });

	public async Task<Handling> NextAsync(CancellationToken cancellationToken) => await _handled.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

	public async Task<IReadOnlyList<Handling>> NextAsync(int count, CancellationToken cancellationToken)
	{
		var handled = new List<Handling>(count);
		for (var i = 0; i < count; i++)
			handled.Add(await NextAsync(cancellationToken).ConfigureAwait(false));
		return handled;
	}
}

/// <summary>Records every message, as handler <c>every</c>.</summary>
internal sealed class RecordingHandler(Handled handled) : IWhatsAppEventHandler<MessageReceived>
{
	public ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		handled.Add("every", e, client);
		return ValueTask.CompletedTask;
	}
}

/// <summary>Records every message, as handler <c>only</c>; the tests register it for one session.</summary>
internal sealed class SessionHandler(Handled handled) : IWhatsAppEventHandler<MessageReceived>
{
	public ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		handled.Add("only", e, client);
		return ValueTask.CompletedTask;
	}
}

/// <summary>Throws <see cref="Failure"/> for message M1.</summary>
internal sealed class FailingHandler : IWhatsAppEventHandler<MessageReceived>
{
	public static InvalidOperationException Failure { get; } = new("the DI handler failed");

	public ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken) =>
		e.Id == MessageId.From("M1") ? throw Failure : ValueTask.CompletedTask;
}

/// <summary>A scoped dependency that knows whether its scope ended.</summary>
internal sealed class ScopedProbe : IAsyncDisposable
{
	public bool Disposed { get; private set; }

	public ValueTask DisposeAsync()
	{
		Disposed = true;
		return ValueTask.CompletedTask;
	}
}

/// <summary>The probe each event's handler got.</summary>
internal sealed class Probes
{
	private readonly Channel<ScopedProbe> _probes = Channel.CreateUnbounded<ScopedProbe>();

	public void Add(ScopedProbe probe) => _ = _probes.Writer.TryWrite(probe);

	public async Task<ScopedProbe> NextAsync(CancellationToken cancellationToken) => await _probes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>Records the <see cref="ScopedProbe"/> it was built with.</summary>
internal sealed class ProbingHandler(ScopedProbe probe, Probes probes) : IWhatsAppEventHandler<MessageReceived>
{
	public ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		probes.Add(probe);
		return ValueTask.CompletedTask;
	}
}

/// <summary>What <see cref="KeyedConsumer"/> saw in its own start.</summary>
internal sealed class Observed
{
	public TaskCompletionSource<(IWhatsAppClient Client, Whatsmeow.Sessions.SessionState State)> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// An application service that takes a keyed client in its constructor and uses it when it starts. Registered before
/// AddWhatsmeow, it is built and started before the library's own hosted service.
/// </summary>
internal sealed class KeyedConsumer([FromKeyedServices("hosting-keyed")] IWhatsAppClient client, WhatsmeowSidecar sidecar, Observed observed) : IHostedService
{
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		var sessions = await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
		observed.Start.SetResult((client, sessions.Single(session => session.Id == client.SessionId).State));
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
