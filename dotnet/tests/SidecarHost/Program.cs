// The parent process of the kill-parent test: it starts the sidecar at args[0], prints the sidecar's process id on
// stdout, and then waits on stdin forever. The test kills this process hard and watches the sidecar exit on its own.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Whatsmeow.Sidecar;

var logs = new FakeLogCollector();
using var provider = new FakeLoggerProvider(logs);
using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));

var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = args[0] }, loggerFactory, CancellationToken.None)
	.ConfigureAwait(false);
await using (sidecar.ConfigureAwait(false))
{
	Console.WriteLine(logs.GetSnapshot().First(r => r.Id.Name == "SidecarStarted").GetStructuredStateValue("ProcessId"));
	await Console.Out.FlushAsync().ConfigureAwait(false);
	_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
}
