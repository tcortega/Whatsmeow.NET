namespace Whatsmeow.Extensions.Hosting;

/// <summary>A handler <see cref="WhatsmeowBuilder.AddHandler{TEvent, THandler}()"/> registered, for one session or every one.</summary>
internal sealed class HandlerRegistration(Type eventType, Type handlerType, SessionId? session, Func<IWhatsAppClient, IServiceProvider, IDisposable> attach)
{
	/// <summary>Whether this registers <paramref name="handler"/> for <paramref name="e"/> on the same sessions, so registering it again adds nothing.</summary>
	public bool Is(Type e, Type handler, SessionId? on) => eventType == e && handlerType == handler && session == on;

	/// <summary>
	/// Registers the handler on <paramref name="client"/> when it is for the client's session, and returns the
	/// registration; null when it is for another session.
	/// </summary>
	public IDisposable? AttachTo(IWhatsAppClient client, IServiceProvider services)
	{
		if (session is { } only && only != client.SessionId)
			return null;

		return attach(client, services);
	}
}
