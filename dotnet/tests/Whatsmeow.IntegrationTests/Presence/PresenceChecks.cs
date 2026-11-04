using Whatsmeow.Events;
using Whatsmeow.Presence;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Presence;

/// <summary>
/// What a contact's typing must look like to the application. The fake-mode presence tests and
/// <c>Whatsmeow.LiveTests</c> check a run with these same assertions (docs/guidelines/tests.md Live WhatsApp); the chat
/// and the sender, in the forms WhatsApp named them, are passed in.
/// </summary>
internal static class PresenceChecks
{
	/// <summary>The contact typed, then paused, in that order, both from <paramref name="source"/>.</summary>
	public static void ValidateTypingThenPaused(IReadOnlyList<ChatPresenceChanged> seen, SessionId session, MessageSource source) =>
		Assert.Equal(
			[
				new ChatPresenceChanged { SessionId = session, Source = source, State = ChatPresenceState.Typing },
				new ChatPresenceChanged { SessionId = session, Source = source, State = ChatPresenceState.Paused },
			],
			seen
		);
}
