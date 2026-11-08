using System.Collections.Immutable;
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
/// every failure is thrown at the end, so the run passes only when every branch did.
/// <para>
/// A business name and a push name change dispatch only when the stored value changes (whatsmeow user.go 679-736)
/// [observed: source], so each branch reads the subject's rows first and fails <c>businessname_precondition_spent</c> or
/// <c>pushname_precondition_spent</c> when its row was set already: a cached store is never a first sight. Branch 2 asks
/// by the first form whose row has no business name, since whatsmeow compares the row of the JID asked (user.go 713).
/// </para>
/// <para>
/// The read back compares the phone number row whole with values none of them read from the store: the saved name the
/// caller was given, the push name this run's text carried, and the verified business name this run's answer carried.
/// Without one of them it fails, <c>savedname_input_missing</c>, <c>readback_pushname_unanchored</c> or
/// <c>readback_businessname_unanchored</c>, and never takes the store's value instead. The LID row is not compared: which
/// form an app state contact mutation names is WhatsApp's [unknown], so its saved name has no expected value.
/// </para>
/// </summary>
internal static class ContactsCycle
{
	/// <summary>How long whatsmeow waits for WhatsApp's answer to a send by default (send.go 155, 213-214) [observed: source].</summary>
	private static readonly TimeSpan s_sendAnswer = TimeSpan.FromSeconds(75);

	/// <summary>
	/// The branches' bounded waits, with <paramref name="watch"/> as its watch: the business name change, then the text's
	/// send, which whatsmeow waits up to 75 s for an answer to, and its arrival, whose push name change is armed with it.
	/// The caller's steps bound their own.
	/// </summary>
	public static TimeSpan Bound(TimeSpan watch) => watch + s_sendAnswer + watch;

	public static async Task RunAsync(ContactsRun run, CancellationToken cancellationToken)
	{
		var failures = new List<ExceptionDispatchInfo>();
		var wire = new Wire();
		await StepAsync(run, failures, "branch 2, business name", t => BusinessNameAsync(run, wire, t), cancellationToken).ConfigureAwait(false);
		await StepAsync(run, failures, "branch 1, push name", t => PushNameAsync(run, wire, t), cancellationToken).ConfigureAwait(false);
		foreach (var step in run.Then)
			await StepAsync(run, failures, step.Name, step.Run, cancellationToken).ConfigureAwait(false);
		await StepAsync(run, failures, "branch 6, saved name read back", t => ReadBackAsync(run, wire, t), cancellationToken).ConfigureAwait(false);

		if (failures.Count == 1)
			failures[0].Throw();
		if (failures.Count > 1)
			throw new AggregateException(failures.Select(f => f.SourceException));
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
#pragma warning disable CA1031 // Justification: thrown at the end of the run, after the other branches ran.
		catch (Exception e)
#pragma warning restore CA1031
		{
			run.Log($"{name}: failed, {e.GetType().Name}");
			failures.Add(ExceptionDispatchInfo.Capture(e));
		}
	}

	/// <summary>
	/// Branch 2: the receiver reads the subject's profile by the first form whose row has no business name, or by its
	/// phone number when both have one; the answer must carry the verified name (#153), and on an unspent row the change
	/// that comes must be <see cref="ContactChecks.BusinessName"/> with it.
	/// </summary>
	private static async Task BusinessNameAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		var receiver = run.Receiver;
		var rows = await RowsAsync(receiver, run.Subject, ct).ConfigureAwait(false);
		var traffic = run.Traffic();
		run.Log(
			$"before branch 2, the subject's rows: {Describe(rows)}; since the count started, its messages: {traffic.Messages}, undecryptable: {traffic.Undecryptable}, business name changes: {traffic.Changes}"
		);
		Assert.True(traffic is (0, 0, 0), "businessname_origin_unclear: the subject sent something, or a business name change of it came, before the query");

		Jid[] forms = run.Subject.Lid is { } lid ? [run.Subject.Chat, lid] : [run.Subject.Chat];
		var unspent = forms.Where(f => rows.SingleOrDefault(r => r.Jid == f)?.BusinessName is null).ToArray();
		var asked = unspent.Length > 0 ? unspent[0] : run.Subject.Chat;
		var spent = unspent.Length == 0;
		run.Log($"branch 2 asks by the {Form(asked)}, whose row had a business name: {spent}");

		var change = receiver.WaitForAsync<ContactUpdated>(e => Names(run.Subject, e.Jid) && e.Change is ContactChange.BusinessName, run.Watch, ct);
		var answer = await receiver.GetUserInfoAsync([asked], ct).ConfigureAwait(false);
		string? name = null;
		run.Redacting(() => name = Assert.Single(answer, a => a.Jid == asked).VerifiedBusinessName);
		wire.BusinessName = name;
		run.Log($"branch 2's answer carried a verified business name: {name is not null}");
		Assert.True(name is not null, "businessname_answer_null: the answer's entry carried no verified business name (#153)");
		Assert.NotEmpty(name);
		Assert.True(!spent, "businessname_precondition_spent: both of the subject's rows had a business name before the query, so no change can come; the answer was checked, the change was not");

		var got = await WithinAsync(change, "the business name change", run.Watch).ConfigureAwait(false);
		run.Redacting(() => Assert.Equal(ContactChecks.BusinessName(receiver.SessionId, asked, name), got));
	}

	/// <summary>
	/// Branch 1: the subject sends a named unavailable presence, then a text; the text must carry a push name, the row of
	/// the form it came from must have had none, and the change must be <see cref="ContactChecks.PushName"/> with it.
	/// The text's push name is kept for the read back whatever the verdict.
	/// </summary>
	private static async Task PushNameAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		var receiver = run.Receiver;
		var rows = await RowsAsync(receiver, run.Subject, ct).ConfigureAwait(false);
		Jid[] forms = run.Subject.Lid is { } lid ? [run.Subject.Chat, lid] : [run.Subject.Chat];
		var pairs = await receiver.ResolveJidsAsync(forms, ct).ConfigureAwait(false);
		run.Log($"before branch 1, the subject's rows: {Describe(rows)}; the store pairs: {string.Join(", ", pairs.Select(p => $"{Form(p.Jid)} with {Form(p.JidAlt)}"))}");
		run.Redacting(() =>
			Assert.All(pairs, p => Assert.True(p.JidAlt is null || IsOtherForm(run.Subject, p.Jid, p.JidAlt.Value), "the store pairs the subject with another account or the same form"))
		);

		var id = run.Id();
		const string Text = "wmn persistent contacts";
		var arrival = receiver.WaitForAsync<MessageReceived>(m => m.Id == id, run.Watch, ct);
		var change = receiver.WaitForAsync<ContactUpdated>(e => Names(run.Subject, e.Jid) && e.Change is ContactChange.PushName, run.Watch, ct);
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
		await run.SendTextAsync(id, Text, ct).ConfigureAwait(false);

		var got = await WithinAsync(arrival, "the subject's text", run.Watch).ConfigureAwait(false);
		var pushName = got.PushName;
		wire.PushName = pushName;
		run.Log($"branch 1's text carried {Carried(pushName)}");
		run.CheckMessage(got, id, Text);
		run.Log($"branch 1's text came from a {Form(got.Source.Sender)} with a {Form(got.Source.SenderAlt)} beside it");
		Assert.True(pushName is not null, "pushname_stimulus_missing: the subject's text carried no push name, so branch 1 has no stimulus");
		Assert.NotEmpty(pushName);

		var sender = got.Source.Sender with { Device = 0 };
		Assert.True(
			rows.SingleOrDefault(r => r.Jid == sender)?.PushName is null,
			"pushname_precondition_spent: the sender's row had a push name before the text, so no first sight is left; the text is stimulus evidence only"
		);
		var senderAlt = got.Source.SenderAlt is { } a ? a with { Device = 0 } : (Jid?)null;
		Assert.True(senderAlt is null || IsOtherForm(run.Subject, sender, senderAlt.Value), "the text's other form is not the subject's other form");
		var alt = senderAlt;
		if (alt is null)
			run.Redacting(() => alt = Assert.Single(pairs, p => p.Jid == sender).JidAlt);

		var updated = await WithinAsync(change, "the push name change", run.Watch).ConfigureAwait(false);
		run.Redacting(() => Assert.Equal(ContactChecks.PushName(receiver.SessionId, sender, alt, pushName), updated));
	}

	/// <summary>
	/// Branch 6: the subject's phone number row, whole, with the saved name the caller was given and the names this run's
	/// text and answer carried. A missing value fails the branch; the store's never stands in for it.
	/// </summary>
	private static async Task ReadBackAsync(ContactsRun run, Wire wire, CancellationToken ct)
	{
		Assert.False(string.IsNullOrWhiteSpace(run.SavedName), "savedname_input_missing: no saved name was given, so the read back has no expected value; the store's is never used instead");
		Assert.True(wire.PushName is { } p && p != "-", "readback_pushname_unanchored: this run's text carried no push name, so the read back has no expected push name");
		Assert.True(wire.BusinessName is not null, "readback_businessname_unanchored: this run's answer carried no verified business name, so the read back has no expected business name");

		var rows = await RowsAsync(run.Receiver, run.Subject, ct).ConfigureAwait(false);
		run.Log($"branch 6 reads back the subject's rows: {Describe(rows)}");
		var expected = new Contact
		{
			Jid = run.Subject.Chat,
			SavedName = run.SavedName,
			PushName = wire.PushName,
			BusinessName = wire.BusinessName,
		};
		run.Redacting(() => Assert.Equal(expected, Assert.Single(rows, r => r.Jid == run.Subject.Chat)));
	}

	/// <summary>What <paramref name="wait"/> got, or a failure naming <paramref name="what"/>, never its content.</summary>
	private static async Task<T> WithinAsync<T>(Task<T> wait, string what, TimeSpan watch)
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

	/// <summary>What a text carried, for the log: never the name.</summary>
	private static string Carried(string? pushName) =>
		pushName switch
		{
			null => "no push name",
			"-" => "a dash",
			_ => "a push name",
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

	/// <summary>What the receiver got from the subject since the count started, before the receiver's first read of it.</summary>
	public required Func<(int Messages, int Undecryptable, int Changes)> Traffic { get; init; }

	/// <summary>Has the subject send a named unavailable presence.</summary>
	public required Func<CancellationToken, Task> SendNamedPresenceAsync { get; init; }

	/// <summary>Has the subject send the receiver a text with the id given.</summary>
	public required Func<MessageId, string, CancellationToken, Task> SendTextAsync { get; init; }

	/// <summary>The text's id.</summary>
	public required Func<MessageId> Id { get; init; }

	/// <summary>Checks the text as the receiver got it, after its lines were logged.</summary>
	public required Action<MessageReceived, MessageId, string> CheckMessage { get; init; }

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
