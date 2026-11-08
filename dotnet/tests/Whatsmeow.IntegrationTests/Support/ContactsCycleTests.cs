using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Messaging;
using Whatsmeow.IntegrationTests.Presence;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Presence;
using Xunit.Sdk;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' Contacts FullCycle on a store that may have seen the subject (<see cref="ContactsCycle"/>, #36, ADR
/// 0062), which no live run can check for itself: that a cached row is never a first sight, that the read back takes no
/// value from the store, that a missing input fails its branch, and that every branch runs and every failure is thrown.
/// The receiver is a session of each test's own on the shared fake-mode sidecar; the fake WhatsApp delivers the subject's
/// text with the push name each test scripts and answers its profile from the directory; the named presence goes out from
/// a second session, as the live subject's does.
/// </summary>
public sealed class ContactsCycleTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const string Business = "Bea's Shop";
	private const string Saved = "Bea Lima";

	[Fact(Timeout = Timeout)]
	public async Task Run_AStoreThatHasNotSeenTheSubject_PassesEveryBranch_InOrder_AndLogsNoName()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-pass", subject: 1, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);

		await ContactsCycle.RunAsync(h.Run("Ana", Saved), ct);

		Assert.Equal(["presence", "text", "then"], h.Calls);
		Assert.Equal(
			[
				"before branch 2, the subject's rows: phone number: saved True, push False, business False; since the count started, its messages: 0, undecryptable: 0, business name changes: 0",
				"branch 2 asks by the phone number, whose row had a business name: False",
				"branch 2's answer carried a verified business name: True",
				"branch 2, business name: passed",
				"before branch 1, the subject's rows: phone number: saved True, push False, business True, LID: saved False, push False, business True; the store pairs: phone number with LID, LID with phone number",
				"branch 1's named unavailable presence: sent",
				"branch 1's text carried a push name",
				"branch 1's text came from a LID with a phone number beside it",
				"branch 1, push name: passed",
				"the caller's step: passed",
				"branch 6 reads back the subject's rows: phone number: saved True, push True, business True, LID: saved False, push True, business True",
				"branch 6, saved name read back: passed",
			],
			h.Lines
		);
		Assert.DoesNotContain(h.Lines, l => l.Contains("Ana", StringComparison.Ordinal) || l.Contains("Bea", StringComparison.Ordinal));
	}

	/// <summary>
	/// An earlier profile read stored the business name on both rows: the answer is still checked, and the branch fails
	/// as spent, since no change can come; the read back still passes on this run's answer.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_BothRowsHadTheBusinessName_FailsBranch2AsSpent_AfterCheckingTheAnswer()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-business-spent", subject: 2, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);
		await h.ReadProfileFirstAsync(ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => ContactsCycle.RunAsync(h.Run("Ana", Saved), ct));

		Assert.StartsWith("businessname_precondition_spent", failed.Message, StringComparison.Ordinal);
		Assert.Contains("branch 2 asks by the phone number, whose row had a business name: True", h.Lines, StringComparer.Ordinal);
		Assert.Contains("branch 2's answer carried a verified business name: True", h.Lines, StringComparer.Ordinal);
		Assert.Equal(["branch 2, business name: failed, TrueException", "branch 1, push name: passed", "the caller's step: passed", "branch 6, saved name read back: passed"], h.Verdicts);
	}

	/// <summary>
	/// The phone number row had the business name and the LID row had none: branch 2 asks by the LID, whose row whatsmeow
	/// compares (user.go 713), and the change names the LID.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_OnlyThePhoneNumberRowHadTheBusinessName_AsksByTheLid_AndTheChangeNamesIt()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-business-lid", subject: 3, Business, ct, registerLid: false);
		await h.SyncSavedNameAsync(Saved, ct);
		await h.ReadProfileFirstAsync(ct);
		await h.RegisterAsync(Business, withLid: true, ct);

		await ContactsCycle.RunAsync(h.Run("Ana", Saved), ct);

		Assert.Contains("branch 2 asks by the LID, whose row had a business name: False", h.Lines, StringComparer.Ordinal);
		Assert.Equal(["branch 2, business name: passed", "branch 1, push name: passed", "the caller's step: passed", "branch 6, saved name read back: passed"], h.Verdicts);
	}

	/// <summary>
	/// The store has the business name from an earlier read, and this answer carries none, as whatsmeow's does at the pin
	/// (#153): branch 2 fails on the answer, and the read back fails unanchored instead of taking the store's name.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheAnswerCarriesNoBusinessNameWhileTheStoreHasOne_FailsBranch2_AndTheReadBackIsUnanchored()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-business-null", subject: 4, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);
		await h.ReadProfileFirstAsync(ct);
		await h.RegisterAsync(businessName: "", withLid: true, ct);

		var failed = await Assert.ThrowsAsync<AggregateException>(() => ContactsCycle.RunAsync(h.Run("Ana", Saved), ct));

		Assert.Collection(
			failed.InnerExceptions,
			e => Assert.StartsWith("businessname_answer_null", e.Message, StringComparison.Ordinal),
			e => Assert.StartsWith("readback_businessname_unanchored", e.Message, StringComparison.Ordinal)
		);
		Assert.Equal(["branch 2, business name: failed, TrueException", "branch 1, push name: passed", "the caller's step: passed", "branch 6, saved name read back: failed, TrueException"], h.Verdicts);
	}

	/// <summary>An earlier message stored the push name: branch 1 fails as spent; the read back uses this run's text's name.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSendersRowHadThePushName_FailsBranch1AsSpent_AndTheReadBackStillPasses()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-push-spent", subject: 5, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);
		await h.DeliverEarlierNamedAsync("Ana", ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => ContactsCycle.RunAsync(h.Run("Ana", Saved), ct));

		Assert.StartsWith("pushname_precondition_spent", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["branch 2, business name: passed", "branch 1, push name: failed, TrueException", "the caller's step: passed", "branch 6, saved name read back: passed"], h.Verdicts);
	}

	/// <summary>The text carries no push name: branch 1 has no stimulus, and the read back has no expected push name.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheTextCarriesNoPushName_FailsBranch1_AndTheReadBackIsUnanchored()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-push-none", subject: 6, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);

		var failed = await Assert.ThrowsAsync<AggregateException>(() => ContactsCycle.RunAsync(h.Run(pushName: null, Saved), ct));

		Assert.Collection(
			failed.InnerExceptions,
			e => Assert.StartsWith("pushname_stimulus_missing", e.Message, StringComparison.Ordinal),
			e => Assert.StartsWith("readback_pushname_unanchored", e.Message, StringComparison.Ordinal)
		);
		Assert.Contains("branch 1's text carried no push name", h.Lines, StringComparer.Ordinal);
	}

	/// <summary>No saved name was given: the read back fails before it reads the store, and every other branch still ran.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_NoSavedNameGiven_FailsTheReadBackAsInputMissing_AndTheOtherBranchesRan()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-saved-missing", subject: 7, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);

		var failed = await Assert.ThrowsAsync<FalseException>(() => ContactsCycle.RunAsync(h.Run("Ana", savedName: null), ct));

		Assert.StartsWith("savedname_input_missing", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["branch 2, business name: passed", "branch 1, push name: passed", "the caller's step: passed", "branch 6, saved name read back: failed, FalseException"], h.Verdicts);
		Assert.DoesNotContain(h.Lines, l => l.StartsWith("branch 6 reads back", StringComparison.Ordinal));
	}

	/// <summary>The saved name given is not the one the store has: the read back fails, so its expected value is the input's.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSavedNameGivenIsNotTheStoredOne_FailsTheReadBack()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-saved-other", subject: 8, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);

		_ = await Assert.ThrowsAsync<EqualException>(() => ContactsCycle.RunAsync(h.Run("Ana", "Bea L."), ct));

		Assert.Equal("branch 6, saved name read back: failed, EqualException", h.Verdicts[^1]);
	}

	/// <summary>The subject sent something before the query: the business name change could be its, so branch 2 fails before asking.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSubjectSentSomethingBeforeTheQuery_FailsBranch2AsOriginUnclear()
	{
		var ct = TestContext.Current.CancellationToken;
		using var h = await Harness.StartAsync(fixture, "contacts-cycle-origin", subject: 9, Business, ct);
		await h.SyncSavedNameAsync(Saved, ct);
		var run = h.Run("Ana", Saved);
		await h.DeliveredAsync("BEFORE", pushName: null, "after the count started", ct);

		var failed = await Assert.ThrowsAsync<AggregateException>(() => ContactsCycle.RunAsync(run, ct));

		Assert.StartsWith("businessname_origin_unclear", failed.InnerExceptions[0].Message, StringComparison.Ordinal);
		Assert.DoesNotContain(h.Lines, l => l.StartsWith("branch 2 asks", StringComparison.Ordinal));
	}

	/// <summary>A receiver, a presence sender and a subject of one test's own, and what the run did through them.</summary>
	private sealed class Harness : IDisposable
	{
		private readonly FakeSidecar _fixture;
		private readonly string _receiverId;
		private readonly IWhatsAppClient _receiver;
		private readonly IWhatsAppClient _sender;
		private readonly string _lid;
		private readonly string _pn;
		private ContactsTraffic? _traffic;

		private Harness(FakeSidecar fixture, string receiverId, IWhatsAppClient receiver, IWhatsAppClient sender, int subject)
		{
			_fixture = fixture;
			_receiverId = receiverId;
			_receiver = receiver;
			_sender = sender;
			_lid = $"98500000{subject}@lid";
			_pn = $"551198500000{subject}@s.whatsapp.net";
		}

		/// <summary>What the run did, in order: <c>presence</c>, <c>text</c> and <c>then</c>.</summary>
		public List<string> Calls { get; } = [];

		/// <summary>What the run logged.</summary>
		public List<string> Lines { get; } = [];

		/// <summary>The branches' verdict lines.</summary>
		public List<string> Verdicts => [.. Lines.Where(l => l.EndsWith(": passed", StringComparison.Ordinal) || l.Contains(": failed, ", StringComparison.Ordinal))];

		/// <summary>Reads the profile for the run; the receiver's own call unless a test replaces it.</summary>
		public Func<Jid, CancellationToken, Task<ImmutableArray<UserInfo>>>? ReadProfile { get; set; }

		/// <summary>Runs inside the named presence's send, before the text, for a test that delivers something first.</summary>
		public Func<CancellationToken, Task> BeforeText { get; set; } = _ => Task.CompletedTask;

		/// <summary>The record the text must be; the fake-mode check unless a test replaces it.</summary>
		public Func<MessageReceived, MessageId, string, MessageReceived>? Expected { get; set; }

		/// <summary>The steps between branch 1 and the read back; one that records <c>then</c> unless a test replaces them.</summary>
		public ImmutableArray<FreshStep>? Then { get; set; }

		public void Dispose() => _traffic?.Dispose();

		/// <summary>The receiver's own profile read.</summary>
		public Task<ImmutableArray<UserInfo>> ReadProfileAsync(Jid jid, CancellationToken cancellationToken) => _receiver.GetUserInfoAsync([jid], cancellationToken);

		/// <summary>Starts the sessions and puts the subject in the directory; <paramref name="subject"/>, a digit, makes its numbers.</summary>
		public static async Task<Harness> StartAsync(FakeSidecar fixture, string id, int subject, string businessName, CancellationToken cancellationToken, bool registerLid = true)
		{
			var receiver = await SessionSetup.ConnectedAsync(fixture, id, cancellationToken).ConfigureAwait(false);
			var sender = await Presences.OnlineAsync(fixture, $"{id}-sender", cancellationToken).ConfigureAwait(false);
			var h = new Harness(fixture, id, receiver, sender, subject);
			await h.RegisterAsync(businessName, registerLid, cancellationToken).ConfigureAwait(false);
			return h;
		}

		/// <summary>
		/// The run, its traffic count started now, after the test's setup, as the live test starts it before connecting,
		/// after everything an earlier run did.
		/// </summary>
		public ContactsRun Run(string? pushName, string? savedName) =>
			new()
			{
				Receiver = _receiver,
				Subject = (Jid.Parse(_pn), Jid.Parse(_lid)),
				Traffic = _traffic ??= new ContactsTraffic(_receiver, (Jid.Parse(_pn), Jid.Parse(_lid))),
				ReadProfileAsync = ReadProfile ?? ReadProfileAsync,
				SendNamedPresenceAsync = async token =>
				{
					Calls.Add("presence");
					await _sender.SendPresenceAsync(PresenceState.Unavailable, token).ConfigureAwait(false);
					await BeforeText(token).ConfigureAwait(false);
				},
				SendTextAsync = async (id, text, token) =>
				{
					Calls.Add("text");
					await DeliverAsync(id.Value, pushName, text, token).ConfigureAwait(false);
				},
				Id = () => MessageId.From("CYCLE1"),
				Expected =
					Expected
					?? ((got, id, text) => MessageChecks.Received(_receiver.SessionId, id, got.Source, got.PushName, got.Timestamp, new TextContent { Text = text })),
				Then =
					Then
					??
				[
					new FreshStep
					{
						Name = "the caller's step",
						Run = _ =>
						{
							Calls.Add("then");
							return Task.CompletedTask;
						},
					},
				],
				SavedName = savedName,
				Redacting = check => check(),
				Log = Lines.Add,
				Watch = TimeSpan.FromSeconds(60),
			};

		/// <summary>Puts the subject in the fake's directory, with or without its LID.</summary>
		public async Task RegisterAsync(string businessName, bool withLid, CancellationToken cancellationToken) =>
			_ = await _fixture
				.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = _pn, Lid = withLid ? _lid : "", BusinessName = businessName }, cancellationToken: cancellationToken)
				.ConfigureAwait(false);

		/// <summary>The app state sync of the subject's saved name, by its phone number.</summary>
		public async Task SyncSavedNameAsync(string fullName, CancellationToken cancellationToken) =>
			_ = await _fixture
				.Fake.SyncContactAsync(new SyncContactRequest { SessionId = _receiverId, Jid = _pn, FullName = fullName }, cancellationToken: cancellationToken)
				.ConfigureAwait(false);

		/// <summary>A profile read before the run, which stores the business name; returns once its change came.</summary>
		public async Task ReadProfileFirstAsync(CancellationToken cancellationToken)
		{
			var change = _receiver.WaitForAsync<ContactUpdated>(e => e.Jid == Jid.Parse(_pn) && e.Change is ContactChange.BusinessName, TimeSpan.FromSeconds(60), cancellationToken);
			_ = await _receiver.GetUserInfoAsync([Jid.Parse(_pn)], cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.BusinessName(_receiver.SessionId, Jid.Parse(_pn), Business), await change.ConfigureAwait(false));
		}

		/// <summary>A message from the subject before the run, carrying <paramref name="pushName"/>; returns once its change came.</summary>
		public async Task DeliverEarlierNamedAsync(string pushName, CancellationToken cancellationToken)
		{
			var change = _receiver.WaitForAsync<ContactUpdated>(e => e.Jid == Jid.Parse(_lid) && e.Change is ContactChange.PushName, TimeSpan.FromSeconds(60), cancellationToken);
			await DeliverAsync("EARLIER", pushName, "before the run", cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.PushName(_receiver.SessionId, Jid.Parse(_lid), Jid.Parse(_pn), pushName), await change.ConfigureAwait(false));
		}

		/// <summary>A message from the subject, as <see cref="DeliverAsync"/>; returns once the receiver got it.</summary>
		public async Task DeliveredAsync(string id, string? pushName, string text, CancellationToken cancellationToken)
		{
			var arrival = _receiver.WaitForAsync<MessageReceived>(m => m.Id == MessageId.From(id), TimeSpan.FromSeconds(60), cancellationToken);
			await DeliverAsync(id, pushName, text, cancellationToken).ConfigureAwait(false);
			_ = await arrival.ConfigureAwait(false);
		}

		/// <summary>A message from the subject by LID, with its phone number beside it, carrying <paramref name="pushName"/>.</summary>
		public async Task DeliverAsync(string id, string? pushName, string text, CancellationToken cancellationToken) =>
			_ = await _fixture
				.Fake.DeliverMessageAsync(
					new DeliverMessageRequest
					{
						SessionId = _receiverId,
						Id = id,
						Chat = _lid,
						Sender = _lid,
						SenderAlt = _pn,
						PushName = pushName ?? "",
						Text = text,
					},
					cancellationToken: cancellationToken
				)
				.ConfigureAwait(false);
	}
}
