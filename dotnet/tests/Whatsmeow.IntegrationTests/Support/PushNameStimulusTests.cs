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
				"push name stimulus 1 (no presence): carried none",
				"push name stimulus 2 (named unavailable presence): presence sent, carried dash",
				"push name stimulus 3 (named available presence): presence sent, carried named",
				"push name stimulus: text 3 named; the sender's LID row had a push name before the first text: False",
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

		Assert.StartsWith("pushname_precondition_spent", failed.Message, StringComparison.Ordinal);
		Assert.Equal(["text 1"], h.Calls);
	}

	/// <summary>
	/// Another message from the sender stores its push name during the ladder, before the named text, which then carries
	/// the same name and changes nothing: the change that came is not the named text's, so it is never a parity pass.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_APushNameChangeCameBeforeTheNamedText_FailsAsOriginUnclear()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "pushname-ladder-origin", sender: 6, senderHasPushName: true, ct);
		h.BeforeText = (k, token) => k == 1 ? h.DeliverNamedAsync("FOREIGN", "Ana", token) : Task.CompletedTask;

		var failed = await Assert.ThrowsAsync<TrueException>(() => PushNameStimulus.RunAsync(h.Ladder([null, "Ana", null]), ct));

		Assert.StartsWith("pushname_origin_unclear", failed.Message, StringComparison.Ordinal);
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

		/// <summary>A message from the sender, not the ladder's, carrying <paramref name="pushName"/>; returns once its change came.</summary>
		public async Task DeliverNamedAsync(string id, string pushName, CancellationToken cancellationToken)
		{
			var change = _receiver.WaitForAsync<ContactUpdated>(e => e.Change is ContactChange.PushName { Name: var name } && name == pushName, TimeSpan.FromSeconds(60), cancellationToken);
			await DeliverAsync(id, pushName, "not the ladder's", cancellationToken).ConfigureAwait(false);
			Assert.Equal(ContactChecks.PushName(_receiver.SessionId, Jid.Parse(_senderLid), Jid.Parse(_senderPn), pushName), await change.ConfigureAwait(false));
		}

		private async Task DeliverAsync(string id, string? pushName, string text, CancellationToken cancellationToken) =>
			_ = await _fixture
				.Fake.DeliverMessageAsync(
					new DeliverMessageRequest
					{
						SessionId = _receiverId,
						Id = id,
						Chat = _senderLid,
						Sender = _senderLid,
						SenderAlt = _senderPn,
						PushName = pushName ?? "",
						Text = text,
					},
					cancellationToken: cancellationToken
				)
				.ConfigureAwait(false);
	}
}
