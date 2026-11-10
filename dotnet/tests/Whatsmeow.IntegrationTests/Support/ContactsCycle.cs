using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// #36's Contacts FullCycle on a store that may have seen the subject before, for the live test on the persistent
/// sessions (ADR 0062): the receiver reads the subject's profile (branch 2), gets a text from it (branch 1), runs the
/// caller's steps (the picture and the about branches), and reads the subject's phone number row back (branch 6). Each
/// branch is checked with the fake-mode records (<see cref="ContactChecks"/>) and keeps its failure while the next runs;
/// every failure is thrown at the end, so the run passes only when every branch did. A cancellation stops the run: no
/// later branch starts, and the cancellation is thrown.
/// <para>
/// A business name and a push name change dispatch only when the stored value changes; a row with no value counts as
/// changed (whatsmeow user.go 679-736, store/sqlstore/store.go 625, 646) [observed: source]. So each branch reads the
/// subject's rows first and fails <c>businessname_precondition_spent</c> or <c>pushname_precondition_spent</c> when its
/// row was set already: a cached store is never a first sight, though a real rename would still bring a change.
/// whatsmeow compares the row of the answer's JID (user.go 248, 271, 714) [observed: source]. That the answer keys an
/// entry under the JID asked was seen for one query of the live account's own LID [observed: ARCHITECTURE §14, the #10
/// run], and for the peer's query of the business account by its LID on a measurement build of the pin with #153's fix
/// [observed: root's #153 measurement, 2026-11-08 03:38:40 UTC]; that it holds at the shipped pin and for other
/// accounts is [inferred]. So branch 2 asks by the first form whose row has no business name and requires exactly one
/// entry for the JID asked.
/// </para>
/// <para>
/// Attribution: a change carries no message id, so the subject's traffic (<see cref="ContactsTraffic"/>) is checked
/// around each change. Branch 2 attributes a change to its query only when the subject sent nothing and no other
/// business name change came from the count's start until the change was taken; branch 1 only when the sender's row
/// had no push name at the branch's start and just before the text, no push name change came before the text was
/// sent, and by the time the change was taken the subject's only message since the branch began was the text. A change
/// stored before a read and delivered after it is still taken as the stimulus's [inferred]: the reads narrow that
/// window, nothing here closes it. The counts are as of .NET dispatch: lanes run concurrently (<c>Dispatcher</c>), a
/// message in its chat's lane and a change in its contact's (<c>EventMapping.Lane</c>) [observed: source], so a subject
/// message in another lane, received before the change, may still be uncounted when a count is read [inferred].
/// </para>
/// <para>
/// The read back compares the phone number row whole with values none of them read from the store: the saved name the
/// caller was given, the push name this run's text carried, and the verified business name this run's answer carried.
/// Without one of them it fails, <c>savedname_input_missing</c>, <c>readback_pushname_unanchored</c> or
/// <c>readback_businessname_unanchored</c>, and never takes the store's value instead. A pass shows the row equals this
/// run's values, not that this run wrote them. The LID row is not compared: which form an app state contact mutation
/// names is WhatsApp's [unknown], so its saved name has no expected value.
/// </para>
/// <para>
/// No failure prints a name: a record mismatch says which fields differ, a count says how many, never a value.
/// </para>
/// </summary>
internal static class ContactsCycle
{
	/// <summary>How long whatsmeow waits for WhatsApp's answer to a send by default (send.go 155, 213-214) [observed: source].</summary>
	private static readonly TimeSpan s_sendAnswer = TimeSpan.FromSeconds(75);

	/// <summary>
	/// The branches' bounded waits, with <paramref name="watch"/> as its watch: branch 2's business name change, then
	/// branch 1's arrival and push name change, both armed before the presence and the text's send, which whatsmeow waits
	/// up to 75 s for an answer to, so each may take that send and a watch. The caller's steps bound their own.
	/// </summary>
	public static TimeSpan Bound(TimeSpan watch) => watch + s_sendAnswer + watch;

	public static async Task RunAsync(ContactsRun run, CancellationToken cancellationToken)
	{
		var failures = new List<ExceptionDispatchInfo>();
		var wire = new Wire();
		(string Name, Func<CancellationToken, Task> Run)[] steps =
		[
			("branch 2, business name", t => BusinessNameAsync(run, wire, t)),
			("branch 1, push name", t => PushNameAsync(run, wire, t)),
			.. run.Then.Select(s => (s.Name, s.Run)),
			("branch 6, saved name read back", t => ReadBackAsync(run, wire, t)),
		];
		foreach (var (name, step) in steps)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				run.Log($"the run was cancelled before {name}; no later step ran");
				if (!failures.Any(f => f.SourceException is OperationCanceledException))
					failures.Add(ExceptionDispatchInfo.Capture(new OperationCanceledException($"the run was cancelled before {name}", cancellationToken)));
				break;
			}

			await StepAsync(run, failures, name, step, cancellationToken).ConfigureAwait(false);
		}

		if (failures.Count == 1)
			failures[0].Throw();
		if (failures.Count > 1)
			throw new AggregateException(failures.Select(f => f.SourceException));
	}

	/// <summary>Refuses a subject and a receiver that name the same account by either form, before any network use.</summary>
	public static void RequireDistinct((Jid Chat, Jid? Lid) subject, (Jid Chat, Jid? Lid) receiver)
	{
		Jid?[] receivers = [receiver.Chat, receiver.Lid];
		Jid?[] subjects = [subject.Chat, subject.Lid];
		Assert.False(
			subjects.Any(j => j is not null && receivers.Contains(j)),
			"contacts_same_account: the live and peer sessions name the same account by phone number or LID, so nothing was connected, read or sent"
		);
	}

	/// <summary>The subject's rows in the receiver's contacts, by its phone number and its LID; never the others.</summary>
	public static async Task<ImmutableArray<Contact>> RowsAsync(IWhatsAppClient receiver, (Jid Chat, Jid? Lid) subject, CancellationToken cancellationToken) =>
		[.. (await receiver.GetContactsAsync(cancellationToken).ConfigureAwait(false)).Where(c => c.Jid == subject.Chat || c.Jid == subject.Lid)];

	/// <summary>Which of each row's names are set, never the names.</summary>
	public static string Describe(ImmutableArray<Contact> rows) =>
		rows.IsEmpty ? "none" : string.Join(", ", rows.Select(r => $"{Form(r.Jid)}: saved {r.SavedName is not null}, push {r.PushName is not null}, business {r.BusinessName is not null}"));

	private static async Task StepAsync(ContactsRun run, List<ExceptionDispatchInfo> failures, string name, Func<CancellationToken, Task> step, CancellationToken cancellationToken)
	{
		try
		{
			await step(cancellationToken).ConfigureAwait(false);
			run.Log($"{name}: passed");
		}
#pragma warning disable CA1031 // Justification: thrown at the end of the run, after the other branches ran, or at once on cancellation.
		catch (Exception e)
#pragma warning restore CA1031
		{
			run.Log($"{name}: failed, {e.GetType().Name}");
			failures.Add(ExceptionDispatchInfo.Capture(e));
		}
	}

	/// <summary>
	/// Branch 2: the receiver reads the subject's profile by the first form whose row has no business name, or by its
	/// phone number when both have one. On an unspent row the change is awaited, or-null, before any verdict, so a bad
	/// answer still records, booleans only, whether the change came; on a spent row no first sight is left, and a change
	/// could come only if the answer's name differs from the stored one (user.go 713-717, store.go 646), so none is
	/// awaited or claimed. Then: exactly one entry for the JID asked, a non-empty verified name in it (#153), an unspent row,
	/// the change, no other traffic of the subject until it was taken, and <see cref="ContactChecks.BusinessName"/> whole.
	/// </summary>
	private static async Task BusinessNameAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		var receiver = run.Receiver;
		var rows = await RowsAsync(receiver, run.Subject, ct).ConfigureAwait(false);
		var before = run.Traffic.Now();
		run.Log(
			$"before branch 2, the subject's rows: {Describe(rows)}; since the count started, its messages: {before.Messages.Length}, undecryptable: {before.Undecryptable}, business name changes: {before.BusinessNames}"
		);
		Assert.True(before is { Messages.Length: 0, Undecryptable: 0, BusinessNames: 0 }, "businessname_origin_unclear: the subject sent something, or a business name change of it came, before the query");

		var unspent = Forms(run.Subject).Where(f => rows.SingleOrDefault(r => r.Jid == f)?.BusinessName is null).ToArray();
		var asked = unspent.Length > 0 ? unspent[0] : run.Subject.Chat;
		var spent = unspent.Length == 0;
		run.Log($"branch 2 asks by the {Form(asked)}, whose row had a business name: {spent}");

		using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct);
		try
		{
			var change = spent ? null : receiver.WaitForAsync<ContactUpdated>(e => Names(run.Subject, e.Jid) && e.Change is ContactChange.BusinessName, run.Watch, waits.Token);
			var answer = await run.ReadProfileAsync(asked, ct).ConfigureAwait(false);
			var entries = answer.Where(a => a.Jid == asked).ToArray();
			var name = entries.Length == 1 ? entries[0].VerifiedBusinessName : null;
			wire.BusinessName = name;
			var got = change is null ? null : await OrNullAsync(change).ConfigureAwait(false);
			var after = run.Traffic.Now();
			run.Log($"branch 2's answer carried a verified business name: {name is { Length: > 0 }}");
			if (change is not null && name is not { Length: > 0 })
				run.Log($"branch 2's answer carried {(name is null ? "no" : "an empty")} verified business name; a business name change came: {got is not null}, naming the JID asked: {got?.Jid == asked}");

			Assert.True(entries.Length == 1, $"businessname_answer_keyed: the answer had {answer.Length} entries, {entries.Length} for the JID asked");
			Assert.True(name is not null, "businessname_answer_null: the answer's entry carried no verified business name (#153)");
			Assert.True(name.Length > 0, "businessname_answer_empty: the answer's entry carried an empty verified business name");
			Assert.True(
				!spent,
				"businessname_precondition_spent: both of the subject's rows had a business name before the query, so no first sight is left; a change could come only from a rename; the answer was checked, no change was awaited"
			);
			Assert.True(got is not null, $"the business name change did not come within {run.Watch.TotalSeconds:F0} s");
			Assert.True(
				after is { Messages.Length: 0, Undecryptable: 0, BusinessNames: 1 },
				$"businessname_origin_unclear: the subject sent {after.Messages.Length} messages and {after.Undecryptable} undecryptable ones, and {after.BusinessNames} business name changes of it came, by the time the change was taken, so it is not attributed to the query"
			);
			Same("the business name change", ContactChecks.BusinessName(receiver.SessionId, asked, name), got);
		}
		finally
		{
			await waits.CancelAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Branch 1: the subject sends a named unavailable presence, then a text. The text must be the expected record and
	/// carry a push name that is neither empty nor a dash; the sender's row must have had none at the branch's start and
	/// just before the send; no push name change may come before the send; by the time the change is taken the text must
	/// be the subject's only message since the branch began; and the change must be <see cref="ContactChecks.PushName"/>.
	/// The text's push name is kept for the read back whatever the verdict. Both waits are armed before the presence, so
	/// each may take the send's answer and a watch.
	/// </summary>
	private static async Task PushNameAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		var receiver = run.Receiver;
		var start = run.Traffic.Now();
		var rows = await RowsAsync(receiver, run.Subject, ct).ConfigureAwait(false);
		var pairs = await receiver.ResolveJidsAsync(Forms(run.Subject), ct).ConfigureAwait(false);
		run.Log($"before branch 1, the subject's rows: {Describe(rows)}; the store pairs: {string.Join(", ", pairs.Select(p => $"{Form(p.Jid)} with {Form(p.JidAlt)}"))}");
		run.Redacting(() =>
			Assert.All(pairs, p => Assert.True(p.JidAlt is null || IsOtherForm(run.Subject, p.Jid, p.JidAlt.Value), "the store pairs the subject with another account or the same form"))
		);

		var id = run.Id();
		const string Text = "wmn persistent contacts";
		var wait = s_sendAnswer + run.Watch;
		using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct);
		try
		{
			var arrival = receiver.WaitForAsync<MessageReceived>(m => m.Id == id, wait, waits.Token);
			var change = receiver.WaitForAsync<ContactUpdated>(e => Names(run.Subject, e.Jid) && e.Change is ContactChange.PushName, wait, waits.Token);
			try
			{
				await run.SendNamedPresenceAsync(ct).ConfigureAwait(false);
			}
			catch (WhatsmeowException e)
			{
				run.Log($"branch 1's named unavailable presence: refused, {e.GetType().Name} {e.ErrorCode}; no text sent");
				throw;
			}

			run.Log("branch 1's named unavailable presence: sent");
			// Just before the send, so a push name stored since the first read shows too.
			var rowsAtSend = await RowsAsync(receiver, run.Subject, ct).ConfigureAwait(false);
			var atSend = run.Traffic.Now();
			await run.SendTextAsync(id, Text, ct).ConfigureAwait(false);

			var got = await WithinAsync(arrival, "the subject's text", wait).ConfigureAwait(false);
			var pushName = got.PushName;
			wire.PushName = pushName;
			run.Log($"branch 1's text carried {Carried(pushName)}");
			var expected = run.Expected(got, id, Text);
			run.Log($"branch 1's text came from a {Form(got.Source.Sender)} with a {Form(got.Source.SenderAlt)} beside it");
			Assert.True(expected.Equals(got), $"the subject's text is not the fake-mode record: {Differences(expected, got)}");
			Assert.True(Named(pushName), "pushname_stimulus_missing: the subject's text carried no push name, or a dash, which whatsmeow stores nothing for, so branch 1 has no stimulus");

			var sender = got.Source.Sender with { Device = 0 };
			Assert.True(
				!HasPushName(rows, sender),
				"pushname_precondition_spent: the sender's row had a push name before the branch began, so no first sight is left; the text is stimulus evidence only"
			);
			Assert.True(
				!HasPushName(rowsAtSend, sender),
				"pushname_precondition_spent: the sender's row gained a push name after the branch began and before the text was sent, so the text found it stored; stimulus evidence only"
			);
			Assert.True(
				atSend.PushNames == start.PushNames,
				"pushname_origin_unclear: a push name change of the subject came after the branch began and before the text was sent, so the change is not the text's"
			);
			var senderAlt = got.Source.SenderAlt is { } a ? a with { Device = 0 } : (Jid?)null;
			Assert.True(senderAlt is null || IsOtherForm(run.Subject, sender, senderAlt.Value), "the text's other form is not the subject's other form");
			var alt = senderAlt;
			if (alt is null)
			{
				var paired = pairs.Where(p => p.Jid == sender).ToArray();
				Assert.True(paired.Length == 1, $"the store paired the sender's form {paired.Length} times, not once");
				alt = paired[0].JidAlt;
			}

			var updated = await WithinAsync(change, "the push name change", wait).ConfigureAwait(false);
			var after = run.Traffic.Now();
			var messages = after.Messages[start.Messages.Length..];
			Assert.True(
				messages.SequenceEqual([id]) && after.Undecryptable == start.Undecryptable && after.PushNames - start.PushNames == 1,
				$"pushname_origin_unclear: since the branch began the subject sent {messages.Length} messages (the text among them: {messages.Contains(id)}) and {after.Undecryptable - start.Undecryptable} undecryptable ones, and {after.PushNames - start.PushNames} push name changes of it came, so the change is not attributed to the text alone"
			);
			Same("the push name change", ContactChecks.PushName(receiver.SessionId, sender, alt, pushName), updated);
		}
		finally
		{
			await waits.CancelAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Branch 6: the subject's phone number row, whole, with the saved name the caller was given and the names this run's
	/// text and answer carried. A missing value fails the branch; the store's never stands in for it.
	/// </summary>
	private static async Task ReadBackAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		Assert.False(string.IsNullOrWhiteSpace(run.SavedName), "savedname_input_missing: no saved name was given, so the read back has no expected value; the store's is never used instead");
		Assert.True(Named(wire.PushName), "readback_pushname_unanchored: this run's text carried no push name, or a dash, so the read back has no expected push name");
		Assert.True(
			wire.BusinessName is { Length: > 0 },
			"readback_businessname_unanchored: this run's answer carried no verified business name, or an empty one, so the read back has no expected business name"
		);

		var rows = await RowsAsync(run.Receiver, run.Subject, ct).ConfigureAwait(false);
		run.Log($"branch 6 reads back the subject's rows: {Describe(rows)}");
		var matches = rows.Where(r => r.Jid == run.Subject.Chat).ToArray();
		Assert.True(matches.Length == 1, $"readback_row_count: the subject has {matches.Length} phone number rows, not 1");
		var expected = new Contact
		{
			Jid = run.Subject.Chat,
			SavedName = run.SavedName,
			PushName = wire.PushName,
			BusinessName = wire.BusinessName,
		};
		var row = matches[0];
		Assert.True(
			expected.Equals(row),
			$"readback_mismatch: the phone number row is not the expected record: saved equal {expected.SavedName == row.SavedName}, push equal {expected.PushName == row.PushName}, business equal {expected.BusinessName == row.BusinessName}"
		);
	}

	/// <summary>Compares a change whole; a mismatch says which fields differ, never a value.</summary>
	internal static void Same(string what, ContactUpdated expected, ContactUpdated? actual) =>
		Assert.True(
			expected.Equals(actual),
			$"{what} is not the fake-mode record: session equal {expected.SessionId == actual?.SessionId}, jid equal {expected.Jid == actual?.Jid}, other form equal {expected.JidAlt == actual?.JidAlt}, change equal {expected.Change.Equals(actual?.Change)}"
		);

	/// <summary>Which of a text's fields equal the expected record's, never a value.</summary>
	private static string Differences(MessageReceived expected, MessageReceived got) =>
		$"session equal {expected.SessionId == got.SessionId}, id equal {expected.Id == got.Id}, source equal {expected.Source.Equals(got.Source)}, push name equal {expected.PushName == got.PushName}, timestamp equal {expected.Timestamp == got.Timestamp}, content equal {expected.Content.Equals(got.Content)}, reply equal {Equals(expected.ReplyTo, got.ReplyTo)}, mentions equal {expected.Mentions.SequenceEqual(got.Mentions)}, ephemeral equal {expected.IsEphemeral == got.IsEphemeral}, destination equal {expected.DestinationChat == got.DestinationChat}";

	/// <summary>What <paramref name="wait"/> got, or a failure naming <paramref name="what"/>, never its content.</summary>
	private static async Task<T> WithinAsync<T>(Task<T> wait, string what, TimeSpan bound)
	{
		try
		{
			return await wait.ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			Assert.Fail($"{what} did not come within {bound.TotalSeconds:F0} s");
			throw;
		}
	}

	/// <summary>What <paramref name="wait"/> got, or null when its bound ended first.</summary>
	private static async Task<T?> OrNullAsync<T>(Task<T> wait)
		where T : class
	{
		try
		{
			return await wait.ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			return null;
		}
	}

	/// <summary>Whether a text's push name is one whatsmeow stores: not none, not empty, not a dash (message.go 59) [observed: source].</summary>
	private static bool Named([NotNullWhen(true)] string? pushName) => pushName is { Length: > 0 } and not "-";

	/// <summary>Whether <paramref name="rows"/> give <paramref name="jid"/> a push name.</summary>
	private static bool HasPushName(ImmutableArray<Contact> rows, Jid jid) => rows.Any(r => r.Jid == jid && r.PushName is not null);

	/// <summary>The subject's forms, its phone number first.</summary>
	private static Jid[] Forms((Jid Chat, Jid? Lid) subject) => subject.Lid is { } lid ? [subject.Chat, lid] : [subject.Chat];

	/// <summary>What a text carried, for the log: never the name.</summary>
	private static string Carried(string? pushName) =>
		pushName switch
		{
			null => "no push name",
			"-" => "a dash",
			_ => "a push name",
		};

	/// <summary>Whether <paramref name="jid"/> names <paramref name="account"/>'s user, by either form, on any device.</summary>
	internal static bool Names((Jid Chat, Jid? Lid) account, Jid jid) => (jid with { Device = 0 }) == account.Chat || (jid with { Device = 0 }) == account.Lid;

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

	/// <summary>What this run's text and answer carried, kept for the read back whatever their branch's verdict.</summary>
	private sealed class Wire
	{
		public string? PushName { get; set; }

		public string? BusinessName { get; set; }
	}
}

/// <summary>What <see cref="ContactsCycle.RunAsync"/> runs, and through what.</summary>
internal sealed record ContactsRun
{
	/// <summary>The receiving session, whose store and events are checked.</summary>
	public required IWhatsAppClient Receiver { get; init; }

	/// <summary>The subject, a verified business, by its phone number and its LID.</summary>
	public required (Jid Chat, Jid? Lid) Subject { get; init; }

	/// <summary>What the receiver got from the subject since the count started, before the receiver's first connection.</summary>
	public required ContactsTraffic Traffic { get; init; }

	/// <summary>Reads the subject's profile as the receiver: the receiver's <c>GetUserInfoAsync</c>.</summary>
	public required Func<Jid, CancellationToken, Task<ImmutableArray<UserInfo>>> ReadProfileAsync { get; init; }

	/// <summary>Has the subject send a named unavailable presence.</summary>
	public required Func<CancellationToken, Task> SendNamedPresenceAsync { get; init; }

	/// <summary>Has the subject send the receiver a text with the id given.</summary>
	public required Func<MessageId, string, CancellationToken, Task> SendTextAsync { get; init; }

	/// <summary>The text's id.</summary>
	public required Func<MessageId> Id { get; init; }

	/// <summary>The fake-mode record the text must be, built from what the receiver got, its id and its text.</summary>
	public required Func<MessageReceived, MessageId, string, MessageReceived> Expected { get; init; }

	/// <summary>The steps between branch 1 and the read back: the picture and the about branches.</summary>
	public required ImmutableArray<FreshStep> Then { get; init; }

	/// <summary>The name the subject is saved under in the receiver account's contacts, from outside the code under test; null when not given.</summary>
	public required string? SavedName { get; init; }

	/// <summary>Runs a record check, its failure redacted where the JIDs are real.</summary>
	public required Action<Action> Redacting { get; init; }

	/// <summary>Writes a line of evidence.</summary>
	public required Action<string> Log { get; init; }

	/// <summary>How long a text and each change may take to come.</summary>
	public required TimeSpan Watch { get; init; }
}
