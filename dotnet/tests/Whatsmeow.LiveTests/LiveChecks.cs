using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Messaging;
using Whatsmeow.IntegrationTests.Support;
using Xunit.Sdk;

namespace Whatsmeow.LiveTests;

/// <summary>
/// What a live test does around the fake-mode checks: it waits, bounded, for what the other account gets, checks that
/// WhatsApp named the sender as one of its forms, and passes the fields WhatsApp sets to the shared checks (for a
/// message, <see cref="MessageChecks.Received"/>), so the rest is compared whole, as in fake mode.
/// </summary>
internal static class LiveChecks
{
	/// <summary>How long a test waits for what WhatsApp delivers to the other account.</summary>
	public static readonly TimeSpan Watch = TimeSpan.FromSeconds(60);

	/// <summary>Whether <paramref name="jid"/> names <paramref name="account"/>'s user, by its phone number or its LID, on any device.</summary>
	public static bool Names((Jid Chat, Jid? Lid) account, Jid jid) => (jid with { Device = 0 }) == account.Chat || (jid with { Device = 0 }) == account.Lid;

	/// <summary>The address form of <paramref name="jid"/>, for the log: the log redacts the user.</summary>
	public static string Form(Jid? jid) =>
		jid switch
		{
			null => "none",
			{ IsLid: true } => "LID",
			{ IsPhoneNumber: true } => "phone number",
			{ } other => other.Server,
		};

	/// <summary>The message with <paramref name="id"/> that <paramref name="receiver"/> gets next; call it before the send.</summary>
	public static Task<MessageReceived> ArrivalAsync(IWhatsAppClient receiver, MessageId id, CancellationToken cancellationToken) =>
		receiver.WaitForAsync<MessageReceived>(m => m.Id == id, Watch, cancellationToken);

	/// <summary>What <paramref name="wait"/> got, or a failure naming <paramref name="what"/> did not come within its bound.</summary>
	public static async Task<T> WithinAsync<T>(Task<T> wait, string what)
	{
		try
		{
			return await wait.ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			Assert.Fail($"{what} did not come within {Watch.TotalSeconds:F0} s");
			throw;
		}
	}

	/// <summary>What <paramref name="wait"/> got, or null when it did not come within its bound: for what is recorded, not checked.</summary>
	public static async Task<T?> OrNullAsync<T>(Task<T> wait)
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

	/// <summary>
	/// The message <paramref name="receiver"/> must have got (<see cref="MessageChecks.Received"/>), from
	/// <paramref name="sender"/>, with <paramref name="content"/>: it checks the source names the sender, not the
	/// receiver, and the chat is <paramref name="group"/>, or in a direct message the sender's, then takes the source,
	/// the push name and the time from <paramref name="got"/>, which WhatsApp set.
	/// </summary>
	public static MessageReceived Received(IWhatsAppClient receiver, MessageReceived got, (Jid Chat, Jid? Lid) sender, MessageId id, MessageContent content, Jid? group = null)
	{
		Assert.False(got.Source.IsFromMe, $"message {id} came as the receiver's own");
		Assert.True(Names(sender, got.Source.Sender), $"message {id} came from a {Form(got.Source.Sender)} that is not the sender's");
		Assert.True(
			group is { } g ? got.Source.Chat == g : Names(sender, got.Source.Chat),
			$"message {id} came in a {Form(got.Source.Chat)} chat that is not {(group is null ? "the sender's" : "the group")}"
		);
		return MessageChecks.Received(receiver.SessionId, id, got.Source, got.PushName, got.Timestamp, content);
	}

	/// <summary>
	/// <see cref="Assert.Equal{T}(T, T)"/>, its failure redacted (<see cref="LogRedaction"/>): a record prints its JIDs,
	/// and a live test's are real phone numbers.
	/// </summary>
	public static void Equal<T>(T expected, T actual) => Redacting(() => Assert.Equal(expected, actual));

	/// <summary><see cref="Assert.Equal{T}(IEnumerable{T}, IEnumerable{T})"/>, its failure redacted.</summary>
	public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Redacting(() => Assert.Equal(expected, actual));

	/// <summary>Runs a check, a shared one included, and fails with its message redacted.</summary>
	public static void Redacting(Action check) => _ = Redacting(() => { check(); return true; });

	/// <inheritdoc cref="Redacting(Action)"/>
	public static T Redacting<T>(Func<T> check)
	{
		try
		{
			return check();
		}
		catch (XunitException e)
		{
#pragma warning disable MA0054 // Justification: the cause's message is the unredacted one; only its redacted text may reach the output.
			throw new XunitException(LogRedaction.Redact(e.Message, LiveSettings.PhoneNumber));
#pragma warning restore MA0054
		}
	}
}
