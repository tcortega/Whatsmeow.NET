using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.IntegrationTests.Contacts;
using Xunit.Sdk;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Branch 5 of #36's Contacts FullCycle on the persistent sessions (ADR 0063): the subject's about text changed once and
/// put back, the receiver checking the change with the fake-mode record (<see cref="ContactChecks.About"/>).
/// <para>
/// whatsmeow reads and writes the about text only: <c>GetUserInfo</c> reads the <c>status</c> node's content and nothing
/// else of it (user.go 257-258), and an unset, empty or not given about all read <c>""</c>; <c>SetStatusMessage</c> takes
/// a text, an emoji and a duration (types/user.go 191-199) [observed: source]. So the branch sets the text alone, never
/// what no read gives back, and checks its restore only as "the text the subject's own read shows equals the one read
/// before"; an emoji or expiry the original had, and the time it was set, are neither read nor put back [inferred].
/// </para>
/// <para>
/// Without the consent it fails before any read or call. It reads the original first and changes nothing when that read
/// fails, names no single entry, is empty, or starts as an earlier run's test text (<see cref="StimulusPrefix"/>). Once
/// the set was sent the change counts as possibly made, whatever its answer: a failed or timed-out write may still apply,
/// so it is never sent again, and the restore runs, on bounds of its own that the caller's token does not cancel. The restore sets the original once and then reads, whatever the set did;
/// it passes only when that read shows the original; its change is logged, and a wait that ended without one (the
/// receiver stopped, say) is logged as none. The body's failure and the restore's are both thrown.
/// </para>
/// <para>
/// No about text leaves the branch: its log lines and failures carry booleans, field names, exception type names and
/// WhatsApp's code of a <see cref="WhatsAppErrorException"/>; an exception a read or a set threw is never kept, inner
/// exceptions included.
/// </para>
/// </summary>
internal static class AboutChange
{
	/// <summary>The consent's one value (ADR 0063): a text-only change, the owner having said the about has no emoji and no expiry.</summary>
	public const string ConsentValue = "text-only";

	/// <summary>
	/// What every test text starts with: <c>wmn about </c> and the UTC time. An original that starts with it is taken as an
	/// earlier run's text, left when that run was killed before its restore [inferred]; an owner's about that happened to
	/// start with it would be refused too, never restored.
	/// </summary>
	public const string StimulusPrefix = "wmn about ";

	/// <summary>How long whatsmeow waits for an IQ's answer by default (request.go 150) [observed: source].</summary>
	private static readonly TimeSpan s_iqAnswer = TimeSpan.FromSeconds(75);

	/// <summary>Each read's and set's own bound: whatsmeow's IQ wait and 15 s for the calls around it.</summary>
	public static readonly TimeSpan CallBound = s_iqAnswer + TimeSpan.FromSeconds(15);

	/// <summary>
	/// The branch's longest run: the body, one read and then a set and a read beside a change wait that times out
	/// <see cref="ChangeWait"/> after it is armed, so 3 calls and a watch; then the restore, a set and a read beside its
	/// own change wait, 2 calls and a watch. Each change wait outlasts the calls it runs beside, so it is the longer one.
	/// </summary>
	public static TimeSpan Bound(TimeSpan callBound, TimeSpan watch) => callBound + ChangeWait(callBound, watch) + ChangeWait(callBound, watch);

	/// <summary>A change wait's timeout from its arming: the set and the read after it, then a watch.</summary>
	public static TimeSpan ChangeWait(TimeSpan callBound, TimeSpan watch) => (2 * callBound) + watch;

	public static async Task RunAsync(AboutRun run, CancellationToken cancellationToken)
	{
		Assert.True(run.Consented, "about_not_authorized: changing the business account's about text needs the owner's consent, which this run does not have, so branch 5 was not run");

		var original = await OriginalAsync(run, cancellationToken).ConfigureAwait(false);
		var stimulus = run.Stimulus();
		Assert.True(stimulus != original, "about_stimulus_not_new: the test's about text equals the original, so a set could not show; nothing was changed");

		var maybeChanged = false;
		Exception? body = null;
		try
		{
			await ChangeAsync(run, stimulus, () => maybeChanged = true, cancellationToken).ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: kept, cleaned of any text, and thrown with the restore's failure below.
		catch (Exception e)
#pragma warning restore CA1031
		{
			body = Clean(e, cancellationToken);
		}

		var restore = maybeChanged ? await RestoreAsync(run, original).ConfigureAwait(false) : null;
		if (body is not null && restore is not null)
			throw new AggregateException(body, restore);
		if ((body ?? restore) is { } failure)
			throw failure;
	}

	/// <summary>The subject's own about, read once; a failed read, no single entry or an empty about changes nothing.</summary>
	private static async Task<string> OriginalAsync(AboutRun run, CancellationToken cancellationToken)
	{
		var answer = await CallAsync(run, "about_original_read_failed", "branch 5's read of the original", t => run.ReadOwnProfileAsync(run.Subject.Chat, t), cancellationToken)
			.ConfigureAwait(false);
		var entries = answer.Where(a => a.Jid == run.Subject.Chat).ToArray();
		Assert.True(entries.Length == 1, $"about_original_unkeyed: the answer had {answer.Length} entries, {entries.Length} for the account's phone number; nothing was changed");
		var original = entries[0].About;
		Assert.True(
			original is { Length: > 0 },
			"about_original_unreadable: the account's own read gave no about text, and an unset, empty or hidden one read the same (user.go 257), so there is nothing to restore; nothing was changed"
		);
		Assert.True(
			!original.StartsWith(StimulusPrefix, StringComparison.Ordinal),
			"about_original_is_test_text: the account's about is an earlier run's test text, left when that run did not restore it, so it is no original to put back; nothing was changed, and only the owner can set the real one from the phone"
		);
		run.Log("branch 5's original about text: readable");
		return original;
	}

	/// <summary>The set, the read that shows it applied, and the change; <paramref name="sending"/> runs just before the set.</summary>
	private static async Task ChangeAsync(AboutRun run, string stimulus, Action sending, CancellationToken cancellationToken)
	{
		var wait = ChangeWait(run.CallBound, run.Watch);
		using var waits = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var change = run.Receiver.WaitForAsync<ContactUpdated>(e => ContactsCycle.Names(run.Subject, e.Jid) && e.Change is ContactChange.About, wait, waits.Token);
		try
		{
			// Before the call: an answer lost on the way may still have changed the about.
			sending();
			_ = await CallAsync(run, "about_set_failed", "branch 5's set", async t => await run.SetAsync(stimulus, t).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
			run.Log("branch 5's set: answered");
			var applied = await AboutAsync(run, "about_applied_read_failed", "branch 5's read after the set", cancellationToken).ConfigureAwait(false) == stimulus;
			run.Log($"branch 5's own read after the set shows the test's text: {applied}");
			Assert.True(applied, "about_not_applied: the set was answered, and the account's own read does not show the test's about text");
			var got = await OrNullAsync(change).ConfigureAwait(false);
			run.Log($"branch 5's about change came: {got is not null}");
			Assert.True(got is not null, $"about_event_missing: no about change naming the business came within {wait.TotalSeconds:F0} s of arming its wait");
			ContactsCycle.Same("the about change", ContactChecks.About(run.Receiver.SessionId, got.Jid, stimulus), got);
		}
		finally
		{
			await EndAsync(waits, change).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// The original set once, then read whatever the set did, on bounds of their own; null when the read shows the
	/// original, else the failure with both outcomes. Whether the restore's change came is logged, not required.
	/// </summary>
	private static async Task<Exception?> RestoreAsync(AboutRun run, string original)
	{
		using var waits = new CancellationTokenSource();
		var change = run.Receiver.WaitForAsync<ContactUpdated>(
			e => ContactsCycle.Names(run.Subject, e.Jid) && e.Change is ContactChange.About { Text: var text } && text == original,
			ChangeWait(run.CallBound, run.Watch),
			waits.Token
		);
		try
		{
			string set;
			try
			{
				_ = await CallAsync(run, "about_restore_set_failed", "the restore's set", async t => await run.SetAsync(original, t).ConfigureAwait(false), CancellationToken.None)
					.ConfigureAwait(false);
				set = "answered";
			}
			catch (CallFailedException e)
			{
				set = e.Outcome;
			}

			bool? equal = null;
			string read;
			try
			{
				equal = await AboutAsync(run, "about_restore_read_failed", "the restore's read", CancellationToken.None).ConfigureAwait(false) == original;
				read = $"equal {equal}";
			}
			catch (CallFailedException e)
			{
				read = e.Outcome;
			}

			var (got, ended) = await ObservedAsync(change).ConfigureAwait(false);
			var same = got is not null && ContactChecks.About(run.Receiver.SessionId, got.Jid, original).Equals(got);
			run.Log($"branch 5's restore: set {set}, own read {read}, its change came {got is not null}{ended}, equal {same}");
			return equal == true
				? null
				: new XunitException($"about_restore_unverified: set {set}, own read {read}; the about text may not be the original, which only the owner can set back from the phone");
		}
		finally
		{
			await EndAsync(waits, change).ConfigureAwait(false);
		}
	}

	/// <summary>The subject's own about now, read once on its bound; no single entry reads as none.</summary>
	private static async Task<string?> AboutAsync(AboutRun run, string reason, string what, CancellationToken cancellationToken)
	{
		var answer = await CallAsync(run, reason, what, t => run.ReadOwnProfileAsync(run.Subject.Chat, t), cancellationToken).ConfigureAwait(false);
		var entries = answer.Where(a => a.Jid == run.Subject.Chat).ToArray();
		return entries.Length == 1 ? entries[0].About : null;
	}

	/// <summary>
	/// A read or a set on its own bound, linked to <paramref name="cancellationToken"/>. A cancellation of that token is
	/// a bare cancellation; any other failure is a <see cref="CallFailedException"/> with the failure's type name and
	/// WhatsApp's code, never its message nor the exception itself, which may carry WhatsApp's text.
	/// </summary>
	private static async Task<T> CallAsync<T>(AboutRun run, string reason, string what, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
	{
		using var bound = new CancellationTokenSource(run.CallBound, run.Time);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
		try
		{
			return await call(linked.Token).ConfigureAwait(false);
		}
#pragma warning disable CA1031, MA0054 // Justification: replaced by a failure without its text, thrown at once; the caught one may carry WhatsApp's text, so it is never embedded.
		catch (Exception e)
		{
			if (cancellationToken.IsCancellationRequested)
				throw new OperationCanceledException($"{what} was cancelled", cancellationToken);
			var outcome = bound.IsCancellationRequested ? $"did not end within {run.CallBound.TotalSeconds:F0} s" : $"failed, {Failure(e)}";
			throw new CallFailedException($"{reason}: {what} {outcome}", outcome);
		}
#pragma warning restore CA1031, MA0054
	}

	/// <summary>A failure's type name, and WhatsApp's code when it refused with one (0 is none), never its message (ADR 0067).</summary>
	private static string Failure(Exception e) => e is WhatsAppErrorException { Code: not 0 } refused ? $"{e.GetType().Name} code {refused.Code}" : e.GetType().Name;

	private static Task<bool> CallAsync(AboutRun run, string reason, string what, Func<CancellationToken, Task> call, CancellationToken cancellationToken) =>
		CallAsync(
			run,
			reason,
			what,
			async t =>
			{
				await call(t).ConfigureAwait(false);
				return true;
			},
			cancellationToken
		);

	/// <summary>The body's failure without any text: an assertion or a call's failure as it is, a cancellation bare, anything else its type name.</summary>
	private static Exception Clean(Exception e, CancellationToken cancellationToken) =>
		e switch
		{
			XunitException => e,
			OperationCanceledException => new OperationCanceledException("branch 5 was cancelled", cancellationToken),
			_ => new XunitException($"branch 5 failed, {e.GetType().Name}"),
		};

	/// <summary>Cancels an armed wait and observes its end, whatever it was.</summary>
	private static async Task EndAsync(CancellationTokenSource waits, Task wait)
	{
		await waits.CancelAsync().ConfigureAwait(false);
		try
		{
			await wait.ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: the wait's end only, after its result was taken or given up.
		catch (Exception)
#pragma warning restore CA1031
		{
			// Cancelled, timed out or ended with the session: nothing of it is kept.
		}
	}

	/// <summary>
	/// The restore's change, which is logged, never required: null when its bound ended first, or when its wait ended
	/// otherwise, as when the receiver stopped (<see cref="ObjectDisposedException"/>), with how, by type name only, so
	/// that end never hides the restore's read or the body's failure.
	/// </summary>
	private static async Task<(ContactUpdated? Got, string Ended)> ObservedAsync(Task<ContactUpdated> wait)
	{
		try
		{
			return (await wait.ConfigureAwait(false), "");
		}
		catch (TimeoutException)
		{
			return (null, "");
		}
#pragma warning disable CA1031 // Justification: the logged-only change; its end is logged by type name and never thrown.
		catch (Exception e)
#pragma warning restore CA1031
		{
			return (null, $" (its wait ended, {e.GetType().Name})");
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

	/// <summary>A read or a set that failed, with its outcome and no text.</summary>
	private sealed class CallFailedException(string message, string outcome) : XunitException(message)
	{
		/// <summary>How the call ended: not within its bound, or failed with an exception's type name.</summary>
		public string Outcome { get; } = outcome;
	}
}

/// <summary>What <see cref="AboutChange.RunAsync"/> runs on.</summary>
internal sealed class AboutRun
{
	/// <summary>Whether the owner's consent was given (<see cref="AboutChange.ConsentValue"/>).</summary>
	public required bool Consented { get; init; }

	/// <summary>The session that must get the change.</summary>
	public required IWhatsAppClient Receiver { get; init; }

	/// <summary>The account whose about changes, by its phone number and its LID.</summary>
	public required (Jid Chat, Jid? Lid) Subject { get; init; }

	/// <summary>The subject's own profile read of the JID given, its phone number.</summary>
	public required Func<Jid, CancellationToken, Task<ImmutableArray<UserInfo>>> ReadOwnProfileAsync { get; init; }

	/// <summary>Sets the subject's about text.</summary>
	public required Func<string, CancellationToken, Task> SetAsync { get; init; }

	/// <summary>The test's about text.</summary>
	public required Func<string> Stimulus { get; init; }

	/// <summary>Each read's and set's own bound.</summary>
	public required TimeSpan CallBound { get; init; }

	/// <summary>How long a change may take after the set and its read.</summary>
	public required TimeSpan Watch { get; init; }

	/// <summary>The clock of the calls' bounds.</summary>
	public required TimeProvider Time { get; init; }

	/// <summary>Where the branch writes its lines.</summary>
	public required Action<string> Log { get; init; }
}
