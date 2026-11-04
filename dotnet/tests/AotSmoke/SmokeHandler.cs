using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;

namespace AotSmoke;

/// <summary>Registered so the compiler builds <c>AddHandler</c>'s generic code, which rooting the assembly leaves out (ADR 0034).</summary>
internal sealed class SmokeHandler : IWhatsAppEventHandler<MessageReceived>
{
	public ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
