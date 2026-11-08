using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Contacts;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Xunit.Sdk;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Branch 5 of the live Contacts FullCycle (<see cref="AboutChange"/>, #36, ADR 0063), which no live run can check for
/// itself: that nothing is read or set without the consent or a readable original, that the restore runs on every path
/// once the change may have been made, is checked by a read and never dropped, and that no about text leaves the branch,
/// in its log or in what it throws.
/// <para>
/// The subject is a user of the fake's directory and the receiver a session of each test's own on the shared fake-mode
/// sidecar. The subject's own read is a second session's profile read of it, so the receiver can stop on its own, and a set is the directory's update and the
/// status notification each test scripts; they prove the branch's order, gates, restore and privacy, not what WhatsApp
/// does with a set (ADR 0063).
/// </para>
/// </summary>
public sealed class AboutChangeTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const string Original = "Original about 7f3a";
	private const string Stimulus = "wmn about 20261108120000";
	private const string Other = "Another about 42";

	[Fact(Timeout = Timeout)]
	public async Task Run_WithoutTheConsent_FailsNotAuthorized_BeforeAnyReadOrSet()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-no-consent", subject: 1, Original, ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(consented: false), ct));

		Assert.StartsWith("about_not_authorized", failed.Message, StringComparison.Ordinal);
		Assert.Equal(0, h.Reads);
		Assert.Empty(h.Sets);
		Assert.Empty(h.Lines);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheReadOfTheOriginalFails_FailsBeforeAnySet_WithoutItsText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-read-fails", subject: 2, Original, ct);
		h.ReadFails = n => n == 1 ? new InvalidOperationException($"read refused: {Original}") : null;

		var failed = await Assert.ThrowsAnyAsync<XunitException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.Equal("about_original_read_failed: branch 5's read of the original failed, InvalidOperationException", failed.Message);
		Assert.Null(failed.InnerException);
		Assert.Empty(h.Sets);
		NoText(h, failed);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheAnswerHasNoEntryForTheAccount_FailsUnkeyed_AndSetsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-unkeyed", subject: 3, Original, ct);
		h.ReadOverride = (_, _) => Task.FromResult(ImmutableArray<UserInfo>.Empty);

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("about_original_unkeyed", failed.Message, StringComparison.Ordinal);
		Assert.Empty(h.Sets);
	}

	/// <summary>Unset, empty and not given read the same (user.go 257), so an empty original is never restored.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_AnEmptyOriginal_FailsUnreadable_AndSetsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-empty", subject: 4, about: "", ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("about_original_unreadable", failed.Message, StringComparison.Ordinal);
		Assert.Equal(1, h.Reads);
		Assert.Empty(h.Sets);
	}

	/// <summary>
	/// A read that gives the account an empty text, not none: the client maps an empty about to none, so only another
	/// reader gives one, and it is refused the same, never restored.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_AnOriginalReadAsEmptyText_FailsUnreadable_AndSetsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-empty-text", subject: 17, Original, ct);
		h.ReadOverride = (jid, _) => Task.FromResult<ImmutableArray<UserInfo>>([new UserInfo { Jid = jid, About = "", PictureId = null, VerifiedBusinessName = null }]);

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("about_original_unreadable", failed.Message, StringComparison.Ordinal);
		Assert.Empty(h.Sets);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheTestsTextEqualsTheOriginal_FailsNotNew_AndSetsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-not-new", subject: 5, Original, ct);

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(stimulus: Original), ct));

		Assert.StartsWith("about_stimulus_not_new", failed.Message, StringComparison.Ordinal);
		Assert.Empty(h.Sets);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheAboutChangesAndComesBack_Passes_RestoresTheOriginal_AndLogsNoText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-pass", subject: 6, Original, ct);

		await AboutChange.RunAsync(h.Run(), ct);

		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Equal(3, h.Reads);
		Assert.Equal(Original, await h.AboutNowAsync(ct));
		Assert.Equal(
			[
				"branch 5's original about text: readable",
				"branch 5's set: answered",
				"branch 5's own read after the set shows the test's text: True",
				"branch 5's about change came: True",
				"branch 5's restore: set answered, own read equal True, its change came True, equal True",
			],
			h.Lines
		);
		NoText(h, failure: null);
	}

	/// <summary>The set answered and the own read still shows the original, #1331's description of the pin's call.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSetDoesNotApply_FailsNotApplied_AndStillRestores()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-not-applied", subject: 7, Original, ct);
		h.Applies = t => t != Stimulus;
		h.Notifies = t => t != Stimulus;

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("about_not_applied", failed.Message, StringComparison.Ordinal);
		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Contains("branch 5's own read after the set shows the test's text: False", h.Lines, StringComparer.Ordinal);
		Assert.Contains("branch 5's restore: set answered, own read equal True, its change came True, equal True", h.Lines, StringComparer.Ordinal);
		NoText(h, failed);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_NoChangeComes_FailsEventMissing_AndStillRestores()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-no-change", subject: 8, Original, ct);
		h.Notifies = t => t != Stimulus;

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(callBound: TimeSpan.FromSeconds(3), watch: TimeSpan.FromSeconds(1)), ct));

		Assert.StartsWith("about_event_missing", failed.Message, StringComparison.Ordinal);
		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Equal(Original, await h.AboutNowAsync(ct));
		Assert.Contains("branch 5's about change came: False", h.Lines, StringComparer.Ordinal);
		NoText(h, failed);
	}

	/// <summary>Only another user's about change comes, with the test's text: it is not the subject's, so the branch fails.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_OnlyAnotherUsersChangeComes_FailsEventMissing()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-other-user", subject: 15, Original, ct);
		h.NotifiedJid = t => t == Stimulus ? "987000099@lid" : null;

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(callBound: TimeSpan.FromSeconds(3), watch: TimeSpan.FromSeconds(1)), ct));

		Assert.StartsWith("about_event_missing", failed.Message, StringComparison.Ordinal);
		NoText(h, failed);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_TheChangeCarriesAnotherText_FailsTheRecord_WithoutEitherText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-other-text", subject: 9, Original, ct);
		h.NotifiedText = t => t == Stimulus ? Other : t;

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("the about change is not the fake-mode record", failed.Message, StringComparison.Ordinal);
		Assert.Contains("change equal False", failed.Message, StringComparison.Ordinal);
		Assert.Equal([Stimulus, Original], h.Sets);
		NoText(h, failed);
	}

	/// <summary>A set whose answer failed may still have been made: it is never sent again, and the restore runs.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheSetFailsAfterApplying_FailsSetFailed_KeepsNoRemoteText_AndRestores()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-set-fails", subject: 10, Original, ct);
		h.FailsAfter = t => t == Stimulus ? new InvalidOperationException($"set refused: {Stimulus}, was {Original}") : null;

		var failed = await Assert.ThrowsAnyAsync<XunitException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.Equal("about_set_failed: branch 5's set failed, InvalidOperationException", failed.Message);
		Assert.Null(failed.InnerException);
		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Equal(Original, await h.AboutNowAsync(ct));
		NoText(h, failed);
	}

	/// <summary>The test's token is cancelled right after the set: the restore still runs, on bounds of its own.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_CancelledAfterTheSet_RestoresOnItsOwnBounds_AndThrowsTheCancellation()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-cancelled", subject: 11, Original, ct);
		using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
		h.AfterSet = async t =>
		{
			if (t == Stimulus)
				await run.CancelAsync().ConfigureAwait(false);
		};

		var failed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AboutChange.RunAsync(h.Run(), run.Token));

		Assert.Null(failed.InnerException);
		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Equal(Original, await h.AboutNowAsync(ct));
		Assert.Contains("branch 5's restore: set answered, own read equal True, its change came True, equal True", h.Lines, StringComparer.Ordinal);
		NoText(h, failed);
	}

	/// <summary>
	/// The body failed and the restore's set failed without applying: the verification read still runs, and both failures
	/// are thrown, the body's first.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheBodyAndTheRestoreFail_ThrowsBoth_AfterTheVerificationRead()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-both-fail", subject: 12, Original, ct);
		h.NotifiedText = t => t == Stimulus ? Other : t;
		h.Applies = t => t != Original;
		h.Notifies = t => t != Original;
		h.FailsAfter = t => t == Original ? new InvalidOperationException($"restore refused: {Original}") : null;

		var failed = await Assert.ThrowsAsync<AggregateException>(() => AboutChange.RunAsync(h.Run(callBound: TimeSpan.FromSeconds(3), watch: TimeSpan.FromSeconds(1)), ct));

		Assert.Collection(
			failed.InnerExceptions,
			e => Assert.StartsWith("the about change is not the fake-mode record", e.Message, StringComparison.Ordinal),
			e => Assert.Equal("about_restore_unverified: set failed, InvalidOperationException, own read equal False; the about text may not be the original, which only the owner can set back from the phone", e.Message)
		);
		Assert.Equal(3, h.Reads);
		Assert.Equal([Stimulus, Original], h.Sets);
		Assert.Contains("branch 5's restore: set failed, InvalidOperationException, own read equal False, its change came False, equal False", h.Lines, StringComparer.Ordinal);
		NoText(h, failed);
	}

	/// <summary>
	/// The receiver stops during the restore, after its set: the restore's change wait ends without a change, which is
	/// logged, not thrown, the read still shows the original, so the body's failure alone is thrown.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheReceiverStopsDuringTheRestore_ThrowsTheBodysFailureAlone_WithTheRestoreVerified()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-receiver-stops", subject: 16, Original, ct);
		h.NotifiedText = t => t == Stimulus ? Other : t;
		h.Notifies = t => t != Original;
		h.AfterSet = async t =>
		{
			if (t == Original)
				await h.DeleteReceiverAsync().ConfigureAwait(false);
		};

		var failed = await Assert.ThrowsAsync<TrueException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.StartsWith("the about change is not the fake-mode record", failed.Message, StringComparison.Ordinal);
		Assert.Contains(
			"branch 5's restore: set answered, own read equal True, its change came False (its wait ended, ObjectDisposedException), equal False",
			h.Lines,
			StringComparer.Ordinal
		);
		Assert.Equal(Original, await h.AboutNowAsync(ct));
		NoText(h, failed);
	}

	/// <summary>The restore's set failed after applying: the read shows the original, so the restore passes, and the log says the set failed.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheRestoresSetFailsButTheReadShowsTheOriginal_Passes_AndLogsTheSetsFailure()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-restore-set-fails", subject: 13, Original, ct);
		h.FailsAfter = t => t == Original ? new InvalidOperationException($"restore refused: {Original}") : null;

		await AboutChange.RunAsync(h.Run(), ct);

		Assert.Contains("branch 5's restore: set failed, InvalidOperationException, own read equal True, its change came True, equal True", h.Lines, StringComparer.Ordinal);
		NoText(h, failure: null);
	}

	/// <summary>The restore's read failed after the body passed: the restore is unverified, with the read's outcome, no text.</summary>
	[Fact(Timeout = Timeout)]
	public async Task Run_TheRestoresReadFails_FailsUnverified_WithoutItsText()
	{
		var ct = TestContext.Current.CancellationToken;
		var h = await Harness.StartAsync(fixture, "about-restore-read-fails", subject: 14, Original, ct);
		h.ReadFails = n => n == 3 ? new InvalidOperationException($"read refused: {Original}") : null;

		var failed = await Assert.ThrowsAnyAsync<XunitException>(() => AboutChange.RunAsync(h.Run(), ct));

		Assert.Equal(
			"about_restore_unverified: set answered, own read failed, InvalidOperationException; the about text may not be the original, which only the owner can set back from the phone",
			failed.Message
		);
		Assert.Null(failed.InnerException);
		NoText(h, failed);
	}

	/// <summary>No about text, the original, the test's or another, in any log line or in anything thrown, inner exceptions and stack included.</summary>
	private static void NoText(Harness h, Exception? failure)
	{
		string[] texts = [Original, Stimulus, Other];
		Assert.DoesNotContain(h.Lines, l => texts.Any(t => l.Contains(t, StringComparison.Ordinal)));
		var thrown = failure?.ToString() ?? "";
		Assert.False(texts.Any(t => thrown.Contains(t, StringComparison.Ordinal)), "what the branch threw carries an about text");
	}

	/// <summary>A receiver of the test's own and a subject in the fake's directory, and what the branch did through them.</summary>
	private sealed class Harness
	{
		private readonly FakeSidecar _fixture;
		private readonly string _receiverId;
		private readonly IWhatsAppClient _receiver;
		private readonly IWhatsAppClient _reader;
		private readonly string _pn;
		private readonly string _lid;

		private Harness(FakeSidecar fixture, string receiverId, IWhatsAppClient receiver, IWhatsAppClient reader, int subject)
		{
			_fixture = fixture;
			_receiverId = receiverId;
			_receiver = receiver;
			_reader = reader;
			_pn = $"55119870000{subject:D2}@s.whatsapp.net";
			_lid = $"9870000{subject:D2}@lid";
		}

		/// <summary>The texts the branch set, in order.</summary>
		public List<string> Sets { get; } = [];

		/// <summary>What the branch logged.</summary>
		public List<string> Lines { get; } = [];

		/// <summary>How many times the branch read the subject's own profile.</summary>
		public int Reads { get; private set; }

		/// <summary>Whether a set changes the directory's about.</summary>
		public Func<string, bool> Applies { get; set; } = _ => true;

		/// <summary>Whether a set makes the fake send the receiver a status notification.</summary>
		public Func<string, bool> Notifies { get; set; } = _ => true;

		/// <summary>The text a set's notification carries.</summary>
		public Func<string, string> NotifiedText { get; set; } = t => t;

		/// <summary>The JID a set's notification names instead of the subject's LID; the subject's by default.</summary>
		public Func<string, string?> NotifiedJid { get; set; } = _ => null;

		/// <summary>What a set throws after it did what it does; none by default.</summary>
		public Func<string, Exception?> FailsAfter { get; set; } = _ => null;

		/// <summary>Runs at the end of a set that threw nothing.</summary>
		public Func<string, Task> AfterSet { get; set; } = _ => Task.CompletedTask;

		/// <summary>What the nth read throws instead of reading; none by default.</summary>
		public Func<int, Exception?> ReadFails { get; set; } = _ => null;

		/// <summary>Replaces the read's answer.</summary>
		public Func<Jid, CancellationToken, Task<ImmutableArray<UserInfo>>>? ReadOverride { get; set; }

		public static async Task<Harness> StartAsync(FakeSidecar fixture, string id, int subject, string about, CancellationToken cancellationToken)
		{
			var receiver = await SessionSetup.ConnectedAsync(fixture, id, cancellationToken).ConfigureAwait(false);
			var reader = await SessionSetup.ConnectedAsync(fixture, $"{id}-reader", cancellationToken).ConfigureAwait(false);
			var h = new Harness(fixture, id, receiver, reader, subject);
			await h.RegisterAsync(about, cancellationToken).ConfigureAwait(false);
			return h;
		}

		public AboutRun Run(bool consented = true, string stimulus = Stimulus, TimeSpan? callBound = null, TimeSpan? watch = null) =>
			new()
			{
				Consented = consented,
				Receiver = _receiver,
				Subject = (Jid.Parse(_pn), Jid.Parse(_lid)),
				ReadOwnProfileAsync = ReadAsync,
				SetAsync = SetAsync,
				Stimulus = () => stimulus,
				CallBound = callBound ?? TimeSpan.FromSeconds(10),
				Watch = watch ?? TimeSpan.FromSeconds(5),
				Time = new FakeTimeProvider(),
				Log = Lines.Add,
			};

		/// <summary>The directory's about now, read by the test itself, not counted.</summary>
		public async Task<string?> AboutNowAsync(CancellationToken cancellationToken) =>
			Assert.Single(await _reader.GetUserInfoAsync([Jid.Parse(_pn)], cancellationToken).ConfigureAwait(false)).About;

		private async Task<ImmutableArray<UserInfo>> ReadAsync(Jid jid, CancellationToken cancellationToken)
		{
			Reads++;
			if (ReadFails(Reads) is { } failure)
				throw failure;
			if (ReadOverride is { } read)
				return await read(jid, cancellationToken).ConfigureAwait(false);
			return await _reader.GetUserInfoAsync([jid], cancellationToken).ConfigureAwait(false);
		}

		private async Task SetAsync(string text, CancellationToken cancellationToken)
		{
			Sets.Add(text);
			if (Applies(text))
				await RegisterAsync(text, cancellationToken).ConfigureAwait(false);
			if (Notifies(text))
				_ = await _fixture
					.Fake.DeliverContactNotificationAsync(
						new DeliverContactNotificationRequest
						{
							SessionId = _receiverId,
							About = new DeliverContactNotificationRequest.Types.About { Jid = NotifiedJid(text) ?? _lid, Text = NotifiedText(text) },
						},
						cancellationToken: cancellationToken
					)
					.ConfigureAwait(false);
			if (FailsAfter(text) is { } failure)
				throw failure;
			await AfterSet(text).ConfigureAwait(false);
		}

		/// <summary>Deletes the receiver's session, which ends every wait of it (<see cref="IWhatsAppClient.WaitForAsync{TEvent}"/>).</summary>
		public Task DeleteReceiverAsync() => _fixture.Sidecar.Sessions.DeleteAsync(SessionId.From(_receiverId), CancellationToken.None);

		private async Task RegisterAsync(string about, CancellationToken cancellationToken) =>
			_ = await _fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = _pn, Lid = _lid, About = about }, cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
