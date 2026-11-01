// Runs the product path under Native AOT; exit code 0 means it worked. args[0] is a whatsmeow-net built at this
// client's version (build/ci.sh builds it): the smoke starts it, calls GetInfo and stops it, then round-trips one
// upstream WhatsApp message.
using Google.Protobuf;
using Whatsmeow;
using Whatsmeow.Proto.WAWebProtobufsE2E;

var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = args[0] }, CancellationToken.None).ConfigureAwait(false);
SidecarInfo info;
await using (sidecar.ConfigureAwait(false))
	info = await sidecar.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);

await Console.Out.WriteLineAsync($"sidecar {info.Version}, protocol {info.Protocol}, whatsmeow {info.WhatsmeowVersion}").ConfigureAwait(false);

var message = new Message { Conversation = "aot smoke" };
var parsed = Message.Parser.ParseFrom(message.ToByteArray());

return parsed.Equals(message) && info.Protocol == 1 ? 0 : 1;
