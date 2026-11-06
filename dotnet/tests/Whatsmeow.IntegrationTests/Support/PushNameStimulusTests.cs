using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Messaging;
using Whatsmeow.IntegrationTests.Presence;
using Whatsmeow.IntegrationTests.Sessions;
using Xunit.Sdk;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' push name stimulus ladder (<see cref="PushNameStimulus"/>, #36), which no live run can check for
/// itself: when it stops, what it logs, and that a cached or unattributable push name is never a parity pass. The
/// receiver is a session of each test's own on the shared fake-mode sidecar, which the fake WhatsApp delivers the
/// sender's texts to with the push name each test scripts; the presences go out from a second session, as the live
/// sender's do, with or without the push name the app state sync sets.
/// </summary>
public sealed class PushNameStimulusTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Run_NoneThenDashThenNamed_SendsEachPresenceBeforeItsText_LogsOnlyCategories_AndPassesTheFakeModeCheck()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-named", sender: 1, senderHasPushName: true, ct);

		await PushNameStimulus.RunAsync(h.Ladder([null, "-", "Ana"]), ct);

		Assert.Equal(["text 1", "presence Unavailable", "text 2", "presence Available", "text 3"], h.Calls);
		Assert.Equal(
			[
				"push name stimulus, before the first text, the sender's rows: none; the store pairs: phone number with none, LID with none",
				"push name stimulus 1: just before its text, the sender's rows: none",
				"push name stimulus 1 (no presence): carried none",
				"push name stimulus 2: just before its text, the sender's rows: none",
				"push name stimulus 2 (named unavailable presence): presence sent, carried dash",
				"push name stimulus 3: just before its text, the sender's rows: none",
				"push name stimulus 3 (named available presence): presence sent, carried named",
				"push name stimulus: text 3 named; the sender's LID row had a push name before the first text: False, just before the named text: False",
				"push name stimulus: the push name change came, checked with the fake-mode check",
			],
			h.Lines
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheFirstTextIsNamed_StopsTheLadderBeforeAnyPresence()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-first", sender: 2, senderHasPushName: true, ct);

		await PushNameStimulus.RunAsync(h.Ladder(["Ana", "Ana", "Ana"]), ct);

		Assert.Equal(["text 1"], h.Calls);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_ThePresenceIsRefusedNoPushName_StopsBeforeItsText_AndFailsAsAMissingStimulus()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-nopushname", sender: 3, senderHasPushName: false, ct);

		var failed = await Assert.ThrowsAsync<FailException>(() => PushNameStimulus.RunAsync(h.Ladder([null, "Ana", "Ana"]), ct));

		Assert.StartsWith("pushname_stimulus_missing: the sender has no push name (NO_PUSH_NAME)", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1", "presence Unavailable"], h.Calls);
		Assert.Equal("push name stimulus 2 (named unavailable presence): presence refused, NO_PUSH_NAME; no text sent, the ladder stops", h.Lines[^1]);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_NoTextIsNamed_FailsAsAMissingStimulusAfterTheThirdText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-none", sender: 4, senderHasPushName: true, ct);

		var failed = await Assert.ThrowsAsync<FailException>(() => PushNameStimulus.RunAsync(h.Ladder([null, "-", null]), ct));

		Assert.StartsWith("pushname_stimulus_missing: none of the 3 texts carried a push name", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1", "presence Unavailable", "text 2", "presence Available", "text 3"], h.Calls);
	}

	/// <summary>
	/// The receiver had the sender's push name before the first text, and the named text changes it, so a push name change
	/// comes and equals the fake-mode check: still a failure, since a cached store is not the first sight.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSendersRowHadAPushNameBefore_FailsAsSpentEvenThoughTheChangeMatches()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-spent", sender: 5, senderHasPushName: true, ct);
		await h.DeliverNamedAsync("EARLIER", "Ana", ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => PushNameStimulus.RunAsync(h.Ladder(["Bia", null, null]), ct));

		Assert.StartsWith("pushname_precondition_spent: the sender's row had a push name before the first text", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1"], h.Calls);
	}

	/// <summary>
	/// Another message from the sender stores its push name after the first text, and the named text then carries the same
	/// name and changes nothing: the row read just before the named text has it, so the ladder says it was spent then,
	/// instead of waiting for a change that cannot come, and the change that came, not the named text's, is never a pass.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_ThePushNameWasStoredBetweenTheTexts_FailsAsSpentBeforeTheNamedText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-between", sender: 6, senderHasPushName: true, ct);
		h.BeforeText = (k, token) => k == 1 ? h.DeliverNamedAsync("FOREIGN", "Ana", token) : Task.CompletedTask;

		var failed = await Assert.ThrowsAsync<TrueException>(() => PushNameStimulus.RunAsync(h.Ladder([null, "Ana", null]), ct));

		Assert.StartsWith("pushname_precondition_spent: the sender's row gained a push name", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1", "presence Unavailable", "text 2"], h.Calls);
	}

	/// <summary>
	/// A message from the sender's phone number, with no LID beside it, stores the push name on that row only (whatsmeow
	/// user.go 679-707), and its change comes, before the named text, which comes by LID with the same name: the LID row was
	/// empty in both reads, but a change of the sender came first, so it is not the text's.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_APushNameChangeOfTheOtherFormCameBeforeTheNamedText_FailsAsOriginUnclear()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-origin", sender: 7, senderHasPushName: true, ct);
		h.BeforeText = (k, token) => k == 1 ? h.DeliverNamedByPhoneNumberAsync("FOREIGN", "Ana", token) : Task.CompletedTask;

		var failed = await Assert.ThrowsAsync<TrueException>(() => PushNameStimulus.RunAsync(h.Ladder([null, "Ana", null]), ct));

		Assert.StartsWith("pushname_origin_unclear", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1", "presence Unavailable", "text 2"], h.Calls);
	}

	/// <summary>
	/// Before the named text another account's message brings its push name, and the sender's picture changes: neither is the
	/// sender's push name change, so the ladder passes on the named text's change.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_OtherContactChangesBeforeTheNamedText_AreNotTheSendersPushName_AndTheLadderPasses()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-traffic", sender: 8, senderHasPushName: true, ct);
		h.BeforeText = (k, token) => k == 1 ? h.DeliverOtherTrafficAsync(token) : Task.CompletedTask;

		await PushNameStimulus.RunAsync(h.Ladder([null, "Ana", null]), ct);

		Assert.Equal(["text 1", "presence Unavailable", "text 2"], h.Calls);
	}

	/// <summary>
	/// A receiver, a presence sender and a sending account of one test's own: the LID map is the container's, shared by
	/// every session of the sidecar (ARCHITECTURE §14), so another test's sender would be paired already. And what the
	/// ladder did through them.
	/// </summary>
	private sealed class Harness
	{
		private readonly FakeSidecar _fixture;
		private readonly string _receiverId;
		private readonly IWhatsAppClient _receiver;
		private readonly IWhatsAppClient _sender;
		private readonly string _senderLid;
		private readonly string _senderPn;

		private Harness(FakeSidecar fixture, string receiverId, IWhatsAppClient receiver, IWhatsAppClient sender, int number)
		{
			_fixture = fixture;
			_receiverId = receiverId;
			_receiver = receiver;
			_sender = sender;
			_senderLid = $"98300000{number}@lid";
			_senderPn = $"551198300000{number}@s.whatsapp.net";
		}

		/// <summary>What the ladder did, in order: <c>presence &lt;state&gt;</c> and <c>text &lt;k&gt;</c>.</summary>
		public List<string> Calls { get; } = [];

		/// <summary>What the ladder logged.</summary>
		public List<string> Lines { get; } = [];

		/// <summary>Runs before text <c>k</c> is delivered, for a test that delivers something else first.</summary>
		public Func<int, CancellationToken, Task> BeforeText { get; set; } = (_, _) => Task.CompletedTask;

		/// <summary>Starts the sessions; <paramref name="sender"/>, a digit, makes the sending account's numbers.</summary>
		public static async Task<Harness> StartAsync(FakeSidecar fixture, string id, int sender, bool senderHasPushName, CancellationToken cancellationToken)
		{
			var receiver = await SessionSetup.ConnectedAsync(fixture, id, cancellationToken).ConfigureAwait(false);
			var presences = senderHasPushName
				? await Presences.OnlineAsync(fixture, $"{id}-sender", cancellationToken).ConfigureAwait(false)
				: await SessionSetup.ConnectedAsync(fixture, $"{id}-sender", cancellationToken).ConfigureAwait(false);
			return new Harness(fixture, id, receiver, presences, sender);
		}

		/// <summary>The ladder, whose text <c>k</c> the fake delivers with <paramref name="carried"/>[k - 1] as its push name, none for null.</summary>
		public PushNameLadder Ladder(string?[] carried) =>
			new()
			{
				Receiver = _receiver,
				Sender = (Jid.Parse(_senderPn), Jid.Parse(_senderLid)),
				SendPresenceAsync = async (state, token) =>
				{
					Calls.Add($"presence {state}");
					await _sender.SendPresenceAsync(state, token).ConfigureAwait(false);
				},
				SendTextAsync = async (k, id, text, token) =>
				{
					await BeforeText(k, token).ConfigureAwait(false);
					Calls.Add($"text {k}");
					await DeliverAsync(id.Value, carried[k - 1], text, token).ConfigureAwait(false);
				},
				Id = k => MessageId.From($"PUSH{k}"),
				CheckMessage = (got, id, text) =>
					Assert.Equal(MessageChecks.Received(_receiver.SessionId, id, got.Source, got.PushName, got.Timestamp, new TextContent { Text = text }), got),
				Redacting = check => check(),
				Log = Lines.Add,
				Watch = TimeSpan.FromSeconds(60),
				Time = new FakeTimeProvider(),
			};

		/// <summary>A message from the sender by LID, not the ladder's, carrying <paramref name="pushName"/>; returns once its change came.</summary>
		public async Task DeliverNamedAsync(string id, string pushName, CancellationToken cancellationToken)
		{
			var change = WaitForChangeAsync(_senderLid, cancellationToken);
			await DeliverAsync(id, _senderLid, _senderPn, pushName, "not the ladder's", cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.PushName(_receiver.SessionId, Jid.Parse(_senderLid), Jid.Parse(_senderPn), pushName), await change.ConfigureAwait(false));
		}

		/// <summary>
		/// A message from the sender's phone number with no LID beside it, before the store pairs the two, carrying
		/// <paramref name="pushName"/>; returns once its change came.
		/// </summary>
		public async Task DeliverNamedByPhoneNumberAsync(string id, string pushName, CancellationToken cancellationToken)
		{
			var change = WaitForChangeAsync(_senderPn, cancellationToken);
			await DeliverAsync(id, _senderPn, senderAlt: "", pushName, "not the ladder's", cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.PushName(_receiver.SessionId, Jid.Parse(_senderPn), jidAlt: null, pushName), await change.ConfigureAwait(false));
		}

		/// <summary>Another account's named message and a picture change of the sender; returns once both changes came.</summary>
		public async Task DeliverOtherTrafficAsync(CancellationToken cancellationToken)
		{
			const string OtherLid = "984000001@lid";
			const string OtherPn = "5511984000001@s.whatsapp.net";
			var named = WaitForChangeAsync(OtherLid, cancellationToken);
			await DeliverAsync("OTHER", OtherLid, OtherPn, "Cy", "not the sender's", cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.PushName(_receiver.SessionId, Jid.Parse(OtherLid), Jid.Parse(OtherPn), "Cy"), await named.ConfigureAwait(false));

			var picture = _receiver.WaitForAsync<ContactUpdated>(e => e.Jid == Jid.Parse(_senderLid) && e.Change is ContactChange.Picture, TimeSpan.FromSeconds(60), cancellationToken);
			_ = await _fixture
				.Fake.DeliverContactNotificationAsync(
					new DeliverContactNotificationRequest
					{
						SessionId = _receiverId,
						Picture = new DeliverContactNotificationRequest.Types.Picture { Jid = _senderLid, Id = "P1" },
					},
					cancellationToken: cancellationToken
				)
				.ConfigureAwait(false);
			Assert.Equal(ContactChecks.Picture(_receiver.SessionId, Jid.Parse(_senderLid), "P1"), await picture.ConfigureAwait(false));
		}

		private Task<ContactUpdated> WaitForChangeAsync(string jid, CancellationToken cancellationToken) =>
			_receiver.WaitForAsync<ContactUpdated>(e => e.Jid == Jid.Parse(jid) && e.Change is ContactChange.PushName, TimeSpan.FromSeconds(60), cancellationToken);

		private Task DeliverAsync(string id, string? pushName, string text, CancellationToken cancellationToken) =>
			DeliverAsync(id, _senderLid, _senderPn, pushName, text, cancellationToken);

		private async Task DeliverAsync(string id, string sender, string senderAlt, string? pushName, string text, CancellationToken cancellationToken) =>
			_ = await _fixture
				.Fake.DeliverMessageAsync(
					new DeliverMessageRequest
					{
						SessionId = _receiverId,
						Id = id,
						Chat = sender,
						Sender = sender,
						SenderAlt = senderAlt,
						PushName = pushName ?? "",
						Text = text,
					},
					cancellationToken: cancellationToken
				)
				.ConfigureAwait(false);
	}
}
