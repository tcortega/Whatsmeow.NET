// Runs the product path under Native AOT; exit code 0 means it worked. args[0] is a whatsmeow-net built at this
// client's version (build/ci.sh builds it): the smoke starts it on a database of its own, calls GetInfo, creates a
// session and lists it, and stops it, then round-trips one upstream WhatsApp message.
using Google.Protobuf;
using Whatsmeow;
using Whatsmeow.Proto.WAWebProtobufsE2E;
using Whatsmeow.Sessions;

var directory = Directory.CreateTempSubdirectory("wmn-aot-");
SidecarInfo info;
IReadOnlyList<SessionInfo> sessions;
try
{
	var options = new WhatsmeowSidecarOptions { SidecarPath = args[0], DatabasePath = Path.Combine(directory.FullName, "whatsmeow-net.db") };
	var sidecar = await WhatsmeowSidecar.StartAsync(options, CancellationToken.None).ConfigureAwait(false);
	await using (sidecar.ConfigureAwait(false))
	{
		info = await sidecar.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);
		_ = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("aot-smoke"), CancellationToken.None).ConfigureAwait(false);
		sessions = await sidecar.Sessions.ListAsync(CancellationToken.None).ConfigureAwait(false);
	}
}
finally
{
	directory.Delete(recursive: true);
}

await Console.Out.WriteLineAsync($"sidecar {info.Version}, protocol {info.Protocol}, whatsmeow {info.WhatsmeowVersion}, {sessions.Count} session").ConfigureAwait(false);

var message = new Message { Conversation = "aot smoke" };
var parsed = Message.Parser.ParseFrom(message.ToByteArray());

var created = sessions is [{ State: SessionState.Created } session] && session.Id == SessionId.From("aot-smoke");
return parsed.Equals(message) && info.Protocol == 1 && created ? 0 : 1;
