using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.Events;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// What a receiver got from the subject of <see cref="ContactsCycle"/> since this count started: the ids of its messages,
/// its undecryptable messages, and its business name and push name changes. Armed before the receiver's first connection,
/// it sees every such event the receiver's handlers see. Its handlers run in the receiver's dispatcher like any other, in
/// registration order (<c>EventPipeline.RunAsync</c>), so for an event a wait armed later also takes, the count already
/// has it when the wait returns [observed: source, <c>EventPipeline.cs</c> 150-170, 216-236].
/// </summary>
internal sealed class ContactsTraffic : IDisposable
{
	private readonly Lock _lock = new();
	private readonly List<MessageId> _messages = [];
	private readonly IDisposable[] _handlers;
	private int _undecryptable;
	private int _businessNames;
	private int _pushNames;

	public ContactsTraffic(IWhatsAppClient receiver, (Jid Chat, Jid? Lid) subject) =>
		_handlers =
		[
			receiver.On<MessageReceived>(
				(e, token) =>
				{
					if (Names(subject, e.Source.Sender))
					{
						lock (_lock)
							_messages.Add(e.Id);
					}

					return ValueTask.CompletedTask;
				}
			),
			receiver.On<UndecryptableMessageReceived>(
				(e, token) =>
				{
					if (Names(subject, e.Source.Sender))
						_ = Interlocked.Increment(ref _undecryptable);
					return ValueTask.CompletedTask;
				}
			),
			receiver.On<ContactUpdated>(
				(e, token) =>
				{
					if (Names(subject, e.Jid) && e.Change is ContactChange.BusinessName)
						_ = Interlocked.Increment(ref _businessNames);
					if (Names(subject, e.Jid) && e.Change is ContactChange.PushName)
						_ = Interlocked.Increment(ref _pushNames);
					return ValueTask.CompletedTask;
				}
			),
		];

	/// <summary>The count now.</summary>
	public (ImmutableArray<MessageId> Messages, int Undecryptable, int BusinessNames, int PushNames) Now()
	{
		lock (_lock)
			return ([.. _messages], Volatile.Read(ref _undecryptable), Volatile.Read(ref _businessNames), Volatile.Read(ref _pushNames));
	}

	public void Dispose()
	{
		foreach (var handler in _handlers)
			handler.Dispose();
	}

	private static bool Names((Jid Chat, Jid? Lid) account, Jid jid) => (jid with { Device = 0 }) == account.Chat || (jid with { Device = 0 }) == account.Lid;
}
