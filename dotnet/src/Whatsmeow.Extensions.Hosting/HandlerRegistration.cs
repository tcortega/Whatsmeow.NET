namespace Whatsmeow.Extensions.Hosting;

/// <summary>A handler <see cref="WhatsmeowBuilder.AddHandler{TEvent, THandler}()"/> registered, for one session or every one.</summary>
internal sealed class HandlerRegistration(SessionId? session, Func<IWhatsAppClient, IServiceProvider, IDisposable> attach)
{
	/// <summary>Registers the handler on a new <paramref name="client"/> when it is for the client's session; it lasts as long as the client.</summary>
	public void AttachTo(IWhatsAppClient client, IServiceProvider services)
	{
		if (session is { } only && only != client.SessionId)
			return;

		_ = attach(client, services);
	}
}
