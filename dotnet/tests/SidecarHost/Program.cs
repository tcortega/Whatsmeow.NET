// The parent process of the kill-parent test: it starts the sidecar at args[0] on the database args[1], prints the sidecar's process id on
// stdout, and then waits on stdin forever. The test kills this process hard and watches the sidecar exit on its own.
// With "events" first, it is the process the events kill test kills instead (EventsHost.cs); with "events-live", the
// live tests' one, on a release build against real WhatsApp.
// With "held-tether" first, the sidecar's stdin stays open in another process after this one dies (HeldTetherHost.cs).
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using SidecarHost;
using Whatsmeow;

if (args is ["events", var fakeSidecar, var database, var session, var blockOn])
{
	await EventsHost.RunAsync(fakeSidecar, database, session, blockOn, fakeWhatsApp: true).ConfigureAwait(false);
	return;
}

if (args is ["events-live", var liveSidecar, var liveDatabase, var liveSession, var liveBlockOn])
{
	await EventsHost.RunAsync(liveSidecar, liveDatabase, liveSession, liveBlockOn, fakeWhatsApp: false).ConfigureAwait(false);
	return;
}

if (args is ["held-tether", var heldSidecar, var heldDatabase])
{
	await HeldTetherHost.RunAsync(heldSidecar, heldDatabase).ConfigureAwait(false);
	return;
}

if (args is ["hold"])
{
	await HeldTetherHost.HoldAsync().ConfigureAwait(false);
	return;
}

var logs = new FakeLogCollector();
using var provider = new FakeLoggerProvider(logs);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));

var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = args[0], DatabasePath = args[1] }, loggerFactory, CancellationToken.None)
	.ConfigureAwait(false);
await using (sidecar.ConfigureAwait(false))
{
	Console.WriteLine(logs.GetSnapshot().First(r => r.Id.Name == "SidecarStarted").GetStructuredStateValue("ProcessId"));
	await Console.Out.FlushAsync().ConfigureAwait(false);
	_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
}
