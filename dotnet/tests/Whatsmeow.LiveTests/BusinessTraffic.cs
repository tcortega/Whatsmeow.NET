using Whatsmeow.Contacts;
using Whatsmeow.Events;

namespace Whatsmeow.LiveTests;

/// <summary>What the companion got from the business since pairing: its messages, undecryptable ones, and business name changes.</summary>
internal sealed class BusinessTraffic : IDisposable
{
	private readonly IDisposable[] _handlers;
	private int _messages;
	private int _undecryptable;
	private int _changes;

	public BusinessTraffic(IWhatsAppClient client, (Jid Chat, Jid? Lid) account) =>
		_handlers =
		[
			client.On<MessageReceived>((e, _) => CountAsync(LiveChecks.Names(account, e.Source.Sender), ref _messages)),
			client.On<UndecryptableMessageReceived>((e, _) => CountAsync(LiveChecks.Names(account, e.Source.Sender), ref _undecryptable)),
			client.On<ContactUpdated>((e, _) => CountAsync(LiveChecks.Names(account, e.Jid) && e.Change is ContactChange.BusinessName, ref _changes)),
		];

	public int Messages => Volatile.Read(ref _messages);

	public int Undecryptable => Volatile.Read(ref _undecryptable);

	public int Changes => Volatile.Read(ref _changes);

	public void Dispose()
	{
		foreach (var handler in _handlers)
			handler.Dispose();
	}

	private static ValueTask CountAsync(bool matches, ref int count)
	{
		if (matches)
			_ = Interlocked.Increment(ref count);
		return ValueTask.CompletedTask;
	}
}
