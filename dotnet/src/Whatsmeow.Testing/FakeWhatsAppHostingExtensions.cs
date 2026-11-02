using Microsoft.Extensions.DependencyInjection.Extensions;
using Whatsmeow.Extensions.Hosting;

namespace Whatsmeow.Testing;

/// <summary>Replaces a host's sessions with <see cref="FakeWhatsAppClient"/>s, for an application's tests.</summary>
public static class FakeWhatsAppHostingExtensions
{
	/// <summary>
	/// Replaces the session of <paramref name="client"/>'s id with <paramref name="client"/>: it is the session's keyed
	/// <see cref="IWhatsAppClient"/>, and the handlers registered with <see cref="WhatsmeowBuilder.AddHandler{TEvent, THandler}()"/>
	/// get its events from the host's start until its stop. A host with a fake starts no sidecar and creates or connects
	/// no session, so every session declared with <see cref="WhatsmeowBuilder.AddSession"/> needs a fake, or the host's
	/// start fails with <see cref="InvalidOperationException"/>; a session created at runtime through
	/// <see cref="WhatsmeowSidecar.Sessions"/> cannot be used. The session is declared if it was not. Call it before or after
	/// the application's own registrations, as in <c>ConfigureTestServices</c>: <c>services.AddWhatsmeow().UseFakeClient(fake)</c>.
	/// The test owns the fake: the host does not dispose it. Replacing a session again replaces the fake.
	/// </summary>
	/// <returns>The builder.</returns>
	public static WhatsmeowBuilder UseFakeClient(this WhatsmeowBuilder builder, FakeWhatsAppClient client)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(client);
		var key = client.SessionId.Value;
		_ = builder.AddSession(key);

		var services = builder.Services;
		_ = services.RemoveAllKeyed<IWhatsAppClient>(key);
		for (var i = services.Count - 1; i >= 0; i--)
		{
			if (!services[i].IsKeyedService && services[i].ImplementationInstance is ReplacedSession replaced && replaced.Id == client.SessionId)
				services.RemoveAt(i);
		}

		_ = services.AddKeyedSingleton<IWhatsAppClient>(key, client);
		_ = services.AddSingleton(new ReplacedSession(client));
		return builder;
	}
}
