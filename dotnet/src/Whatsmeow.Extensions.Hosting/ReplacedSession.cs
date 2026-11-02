namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// A declared session whose keyed client a test replaced with a fake (<c>Whatsmeow.Testing</c>, ADR 0036). A host with
/// one starts no sidecar: the registered handlers go on the fakes instead.
/// </summary>
internal sealed class ReplacedSession(IWhatsAppClient client)
{
	public IWhatsAppClient Client { get; } = client;

	public SessionId Id => Client.SessionId;
}
