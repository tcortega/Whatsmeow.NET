using Whatsmeow.ChatState;

namespace Whatsmeow.IntegrationTests.ChatState;

/// <summary>
/// What a chat write's echo must be. The fake-mode chat state tests and <c>Whatsmeow.LiveTests</c> check a run with
/// these same assertions (docs/guidelines/tests.md Live WhatsApp).
/// </summary>
internal static class ChatStateChecks
{
	/// <summary>A mute until <paramref name="until"/> comes back as a change of the chat, not from a full sync, with its end.</summary>
	public static void ValidateMuteEcho(ChatStateChanged echo, Jid chat, DateTimeOffset until) =>
		Assert.Equal((chat, false, new ChatStateChange.Mute { IsMuted = true, Until = until }), (echo.Chat, echo.FromFullSync, echo.Change));
}
