using EchoConsole;
using Whatsmeow;

// One WhatsApp session that echoes every text it gets, without DI. From NuGet the sidecar comes with the package; in
// this repository, set WHATSMEOW_NET_SIDECAR_PATH to a whatsmeow-net you built. Scan the QR code with the phone:
// WhatsApp, Linked devices, Link a device.
var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions(), CancellationToken.None).ConfigureAwait(false);
await using (sidecar.ConfigureAwait(false))
{
	var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("main"), CancellationToken.None).ConfigureAwait(false);
	using var echo = await Echo.StartAsync(client, Console.Out, CancellationToken.None).ConfigureAwait(false);

	await Console.Out.WriteLineAsync("Echoing every text; press Enter to stop.").ConfigureAwait(false);
	_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
}
