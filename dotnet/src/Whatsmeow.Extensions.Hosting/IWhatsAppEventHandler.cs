using Whatsmeow.Events;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// Handles each event of type <typeparamref name="TEvent"/>, registered with
/// <see cref="WhatsmeowBuilder.AddHandler{TEvent, THandler}()"/>. Each event runs in a DI scope of its own, so a scoped
/// dependency, such as a <c>DbContext</c>, is new for each event. It runs in the same dispatcher as
/// <see cref="IWhatsAppClient.On{TEvent}"/>, with the same rules: events of one chat reach it one at a time, in order;
/// the event is acknowledged only after every handler returned; a handler that throws raises
/// <see cref="IWhatsAppClient.OnHandlerError"/>, and the event counts as handled. Delivery is at least once, so a
/// handler must be idempotent: after a crash of either process an event can come again, with the same
/// <see cref="WhatsAppEvent.SessionId"/> and message id, which together are the dedupe key.
/// </summary>
/// <typeparam name="TEvent">The events it handles; <see cref="WhatsAppEvent"/> for every event.</typeparam>
[SuppressMessage(
	"Naming",
	"CA1711:Identifiers should not have incorrect suffix",
	Justification = "The name ARCHITECTURE §8.4 and #39 give the DI handler; it handles WhatsApp events and is no .NET event delegate."
)]
public interface IWhatsAppEventHandler<TEvent>
	where TEvent : WhatsAppEvent
{
	/// <summary>Handles <paramref name="e"/>, an event of the session of <paramref name="client"/>.</summary>
	/// <param name="e">The event.</param>
	/// <param name="client">The client of the event's session, to answer on.</param>
	/// <param name="cancellationToken">
	/// Cancelled when the host stops or the session is deleted; a handler that ends while it is cancelled has not handled
	/// the event, which comes again in the next process.
	/// </param>
	ValueTask HandleAsync(TEvent e, IWhatsAppClient client, CancellationToken cancellationToken);
}
