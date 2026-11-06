using System.Collections.Immutable;
using System.Threading.Channels;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.Presence;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// #36's push name stimulus ladder, for the live diagnostic on the persistent sessions (ADR 0061): the sender sends up to
/// three texts to the receiver, the first with no presence before it on the connection, the second after a named
/// unavailable presence, the third after a named available one, and stops at the first text that carries a push name.
/// A message's push name is the stanza's <c>notify</c> (whatsmeow message.go 235), and the one stanza whatsmeow sends
/// with the account's push name is a presence (presence.go 60-87) [observed: source]; whether WhatsApp attaches
/// <c>notify</c> after one is [unknown], which the ladder measures.
/// <para>
/// Each text's line names its variant and what it carried, none, dash or named, never the name. A dash, which whatsmeow
/// stores nothing for (message.go 59), continues the ladder; a presence refused <c>NO_PUSH_NAME</c> is recorded and stops
/// it, with no text after it. On the first named text it checks the fake-mode record: the sender's row in the receiver's
/// store, read before the first text, had no push name (the first sight, whatsmeow user.go 679-707), no push name change
/// came before that text was sent, and the change that came equals <see cref="ContactChecks.PushName"/> whole, its other
/// form from the message or else the store's pair read before the first text, as whatsmeow takes it. A row that had one
/// fails <c>pushname_precondition_spent</c>: the texts are evidence of the stimulus, never a parity pass.
/// </para>
/// </summary>
internal static class PushNameStimulus
{
	/// <summary>The presence before each text, in order; null for none.</summary>
	private static readonly ImmutableArray<PresenceState?> s_presences = [null, PresenceState.Unavailable, PresenceState.Available];

	public static async Task RunAsync(PushNameLadder ladder, CancellationToken cancellationToken)
	{
		var receiver = ladder.Receiver;
		var sender = ladder.Sender;
		var changes = Channel.CreateUnbounded<ContactUpdated>();
		// Armed before the rows are read, so a change from anything before the first text is seen too.
		using var watch = receiver.On<ContactUpdated>(
			(e, token) =>
			{
				if (Names(sender, e.Jid) && e.Change is ContactChange.PushName)
					_ = changes.Writer.TryWrite(e);
				return ValueTask.CompletedTask;
			}
		);

		var rows = await RowsAsync(receiver, sender, cancellationToken).ConfigureAwait(false);
		var pairs = await receiver.ResolveJidsAsync([sender.Chat, sender.Lid!.Value], cancellationToken).ConfigureAwait(false);
		ladder.Log(
			$"push name stimulus, before the first text, the sender's rows: {Describe(rows)}; the store pairs: {string.Join(", ", pairs.Select(p => $"{Form(p.Jid)} with {Form(p.JidAlt)}"))}"
		);
		Assert.All(pairs, p => Assert.True(p.JidAlt is null || IsOtherForm(sender, p.Jid, p.JidAlt.Value), "the store pairs the sender with another account or the same form"));

		MessageReceived? named = null;
		var namedText = 0;
		var changesBefore = 0;
		for (var k = 1; k <= s_presences.Length && named is null; k++)
		{
			var presence = s_presences[k - 1];
			var line = $"push name stimulus {k} ({Variant(presence)}): ";
			if (presence is { } state)
			{
				try
				{
					await ladder.SendPresenceAsync(state, cancellationToken).ConfigureAwait(false);
				}
				catch (WhatsmeowException e) when (e.ErrorCode is WhatsmeowErrorCode.NoPushName)
				{
					ladder.Log($"{line}presence refused, NO_PUSH_NAME; no text sent, the ladder stops");
					Assert.Fail("pushname_stimulus_missing: the sender has no push name (NO_PUSH_NAME), so it can send no named presence, and the ladder stopped");
				}
				catch (WhatsmeowException e)
				{
					ladder.Log($"{line}presence refused, {e.GetType().Name} {e.ErrorCode}; no text sent");
					throw;
				}

				line += "presence sent, ";
			}

			var id = ladder.Id(k);
			var text = $"wmn push name {k}";
			var arrival = receiver.WaitForAsync<MessageReceived>(m => m.Id == id, ladder.Watch, cancellationToken);
			changesBefore = changes.Reader.Count;
			await ladder.SendTextAsync(k, id, text, cancellationToken).ConfigureAwait(false);
			var got = await WithinAsync(arrival, $"text {k}", ladder.Watch).ConfigureAwait(false);
			var carried = Carried(got.PushName);
			ladder.Log($"{line}carried {carried}");
			ladder.CheckMessage(got, id, text);
			if (carried is "named")
				(named, namedText) = (got, k);
		}

		if (named is null)
			Assert.Fail($"pushname_stimulus_missing: none of the {s_presences.Length} texts carried a push name, so #36's push-name branch stays unfulfilled live");

		var from = named.Source.Sender with { Device = 0 };
		var spent = rows.SingleOrDefault(r => r.Jid == from)?.PushName is not null;
		ladder.Log($"push name stimulus: text {namedText} named; the sender's {Form(from)} row had a push name before the first text: {spent}");
		Assert.True(!spent, "pushname_precondition_spent: the sender's row had a push name before the first text, so the texts are stimulus evidence only, never a parity pass");
		Assert.True(changesBefore == 0, "pushname_origin_unclear: a push name change of the sender came before the named text was sent, so the change is not the text's");
		var fromAlt = named.Source.SenderAlt is { } a ? a with { Device = 0 } : (Jid?)null;
		Assert.True(fromAlt is null || IsOtherForm(sender, from, fromAlt.Value), "the text's other form is not the sender's other form");
		var alt = fromAlt ?? Assert.Single(pairs, p => p.Jid == from).JidAlt;

		var change = await NextAsync(changes.Reader, ladder, cancellationToken).ConfigureAwait(false);
		ladder.Redacting(() => Assert.Equal(ContactChecks.PushName(receiver.SessionId, from, alt, named.PushName!), change));
		ladder.Log("push name stimulus: the push name change came, checked with the fake-mode check");
	}

	/// <summary>The sender's rows in the receiver's contacts, by its phone number and its LID; never the others.</summary>
	public static async Task<ImmutableArray<Contact>> RowsAsync(IWhatsAppClient receiver, (Jid Chat, Jid? Lid) sender, CancellationToken cancellationToken) =>
		[.. (await receiver.GetContactsAsync(cancellationToken).ConfigureAwait(false)).Where(c => c.Jid == sender.Chat || c.Jid == sender.Lid)];

	/// <summary>Which of each row's names are set, never the names.</summary>
	public static string Describe(ImmutableArray<Contact> rows) =>
		rows.IsEmpty ? "none" : string.Join(", ", rows.Select(r => $"{Form(r.Jid)}: saved {r.SavedName is not null}, push {r.PushName is not null}, business {r.BusinessName is not null}"));

	/// <summary>The text <paramref name="wait"/> got, or a failure naming <paramref name="what"/>, never its content.</summary>
	private static async Task<MessageReceived> WithinAsync(Task<MessageReceived> wait, string what, TimeSpan watch)
	{
		try
		{
			return await wait.ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			Assert.Fail($"{what} did not come within {watch.TotalSeconds:F0} s");
			throw;
		}
	}

	/// <summary>The first push name change, or a failure when none came within the ladder's watch.</summary>
	private static async Task<ContactUpdated> NextAsync(ChannelReader<ContactUpdated> changes, PushNameLadder ladder, CancellationToken cancellationToken)
	{
		using var bound = new CancellationTokenSource(ladder.Watch, ladder.Time);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
		try
		{
			return await changes.ReadAsync(linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			Assert.Fail($"the push name change did not come within {ladder.Watch.TotalSeconds:F0} s");
			throw;
		}
	}

	private static string Variant(PresenceState? presence) =>
		presence switch
		{
			null => "no presence",
			PresenceState.Unavailable => "named unavailable presence",
			PresenceState.Available => "named available presence",
			_ => throw new ArgumentOutOfRangeException(nameof(presence), presence, "a presence the ladder does not send"),
		};

	/// <summary>What a text carried, for the log: never the name.</summary>
	private static string Carried(string? pushName) =>
		pushName switch
		{
			null => "none",
			"-" => "dash",
			_ => "named",
		};

	/// <summary>Whether <paramref name="jid"/> names <paramref name="account"/>'s user, by either form, on any device.</summary>
	private static bool Names((Jid Chat, Jid? Lid) account, Jid jid) => (jid with { Device = 0 }) == account.Chat || (jid with { Device = 0 }) == account.Lid;

	/// <summary>Whether <paramref name="alt"/> names <paramref name="account"/> in the form <paramref name="jid"/> is not.</summary>
	private static bool IsOtherForm((Jid Chat, Jid? Lid) account, Jid jid, Jid alt) => Names(account, alt) && alt.IsLid != jid.IsLid;

	/// <summary>The address form of <paramref name="jid"/>, for the log.</summary>
	private static string Form(Jid? jid) =>
		jid switch
		{
			null => "none",
			{ IsLid: true } => "LID",
			{ IsPhoneNumber: true } => "phone number",
			{ } other => other.Server,
		};
}

/// <summary>What <see cref="PushNameStimulus.RunAsync"/> sends, and through what.</summary>
internal sealed record PushNameLadder
{
	/// <summary>The receiving session, whose store and events are checked.</summary>
	public required IWhatsAppClient Receiver { get; init; }

	/// <summary>The sending account, by its phone number and its LID.</summary>
	public required (Jid Chat, Jid? Lid) Sender { get; init; }

	/// <summary>Sends the sender's presence.</summary>
	public required Func<PresenceState, CancellationToken, Task> SendPresenceAsync { get; init; }

	/// <summary>Sends text <c>k</c>, from 1, to the receiver with the id and text given.</summary>
	public required Func<int, MessageId, string, CancellationToken, Task> SendTextAsync { get; init; }

	/// <summary>The id of text <c>k</c>.</summary>
	public required Func<int, MessageId> Id { get; init; }

	/// <summary>Checks a text as the receiver got it, after its line was logged.</summary>
	public required Action<MessageReceived, MessageId, string> CheckMessage { get; init; }

	/// <summary>Runs the record check, its failure redacted where the JIDs are real.</summary>
	public required Action<Action> Redacting { get; init; }

	/// <summary>Writes a line of evidence.</summary>
	public required Action<string> Log { get; init; }

	/// <summary>How long a text and the push name change may take to come.</summary>
	public required TimeSpan Watch { get; init; }

	/// <summary>The clock that bounds the wait for the change.</summary>
	public required TimeProvider Time { get; init; }
}
