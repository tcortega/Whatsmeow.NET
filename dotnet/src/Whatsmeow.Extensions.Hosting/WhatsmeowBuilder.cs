using Microsoft.Extensions.DependencyInjection.Extensions;
using Whatsmeow.Events;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>Declares the sessions of the sidecar <see cref="WhatsmeowServiceCollectionExtensions.AddWhatsmeow"/> added, and registers handlers for their events.</summary>
public sealed class WhatsmeowBuilder
{
	private readonly IServiceCollection _services;

	internal WhatsmeowBuilder(IServiceCollection services) => _services = services;

	/// <summary>
	/// Declares the session <paramref name="sessionKey"/>, resolved as <c>[FromKeyedServices("key")] IWhatsAppClient</c>,
	/// also before the host started. When the host starts, the session is created if it does not exist, and connected if
	/// it is paired and <see cref="Sessions.SessionState.Disconnected"/>, before any other hosted service starts; a
	/// session that is not paired waits for the application to pair it, and one in a stopping state keeps it. A connect
	/// that fails makes the host's start fail. Declaring a session again does nothing.
	/// </summary>
	/// <param name="sessionKey">The session's id, which is also its service key.</param>
	/// <exception cref="WhatsmeowValidationException"><paramref name="sessionKey"/> is not a session id.</exception>
	public WhatsmeowBuilder AddSession(string sessionKey)
	{
		var id = SessionId.From(sessionKey);
		if (_services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationInstance is DeclaredSession declared && declared.Id == id))
			return this;

		_ = _services.AddSingleton(new DeclaredSession(id));
		_services.TryAddKeyedSingleton<IWhatsAppClient>(
			sessionKey,
			static (services, key) => services.GetRequiredService<WhatsmeowSidecar>().Sessions.Client(SessionId.From((string)key!))
		);
		return this;
	}

	/// <summary>
	/// Registers <typeparamref name="THandler"/>, a scoped service, for the <typeparamref name="TEvent"/> events of every
	/// session: the declared ones and the ones created at runtime through <see cref="WhatsmeowSidecar.Sessions"/>.
	/// </summary>
	public WhatsmeowBuilder AddHandler<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
		where TEvent : WhatsAppEvent
		where THandler : class, IWhatsAppEventHandler<TEvent> =>
		Register<TEvent, THandler>(session: null);

	/// <summary>Registers <typeparamref name="THandler"/>, a scoped service, for the <typeparamref name="TEvent"/> events of the session <paramref name="sessionKey"/> only.</summary>
	/// <exception cref="WhatsmeowValidationException"><paramref name="sessionKey"/> is not a session id.</exception>
	public WhatsmeowBuilder AddHandler<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(string sessionKey)
		where TEvent : WhatsAppEvent
		where THandler : class, IWhatsAppEventHandler<TEvent> =>
		Register<TEvent, THandler>(SessionId.From(sessionKey));

	/// <summary>Starts the sidecar with <c>--fake-whatsapp</c>, for the integration tests (ADR 0019); a release build refuses it.</summary>
	internal WhatsmeowBuilder UseFakeWhatsApp()
	{
		_services.TryAddSingleton<FakeWhatsAppMode>();
		return this;
	}

	private WhatsmeowBuilder Register<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(SessionId? session)
		where TEvent : WhatsAppEvent
		where THandler : class, IWhatsAppEventHandler<TEvent>
	{
		_services.TryAddScoped<THandler>();
		_ = _services.AddSingleton(
			new HandlerRegistration(session, static (client, services) => client.On<TEvent>((e, ct) => HandleAsync<TEvent, THandler>(services, client, e, ct)))
		);
		return this;
	}

	/// <summary>Runs one event's handler in a scope of its own.</summary>
	private static async ValueTask HandleAsync<TEvent, THandler>(IServiceProvider services, IWhatsAppClient client, TEvent e, CancellationToken cancellationToken)
		where TEvent : WhatsAppEvent
		where THandler : class, IWhatsAppEventHandler<TEvent>
	{
		var scope = services.CreateAsyncScope();
		await using (scope.ConfigureAwait(false))
			await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(e, client, cancellationToken).ConfigureAwait(false);
	}
}
