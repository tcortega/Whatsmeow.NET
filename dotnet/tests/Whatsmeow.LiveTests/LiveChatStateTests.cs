using System.Globalization;
using Whatsmeow.ChatState;
using Whatsmeow.IntegrationTests.ChatState;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Chat state (#38) against real WhatsApp, the owner at the live account's phone: the questions #38 left [unknown], what
/// the phone shows for a mute's end and for a chat marked read or unread with no message key. Each write is put back.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveChatStateTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>
	/// The chat with the peer muted until a minute three days ahead, with milliseconds: the write comes back as its
	/// change with that end, as in fake mode (<see cref="ChatStateChecks"/>), and the owner says what end the phone
	/// shows, which tells whether WhatsApp reads it in milliseconds or seconds. It is unmuted after.
	/// </summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true)]
	public async Task Live_MuteTheChatUntilAnInstant_TheEchoHasIt_AndTheOwnerSaysWhatEndThePhoneShows()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var (chat, _) = await peer.AccountAsync(ct);
		var now = LiveClock.Time.GetUtcNow();
		var until = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddDays(3).AddMilliseconds(678);
		var echo = client.WaitForAsync<ChatStateChanged>(c => c.Change is ChatStateChange.Mute, LiveChecks.Watch, ct);
		try
		{
			await client.MuteChatAsync(chat, until, ct);
			var change = await LiveChecks.WithinAsync(echo, "the mute's echo");
			live.Write($"live: the mute's echo came for a {LiveChecks.Form(change.Chat)} chat: {change.Change}");
			LiveChecks.Redacting(() => ChatStateChecks.ValidateMuteEcho(change, chat, until));

			_ = await live.AskOwnerAsync(
				$"On the live account's phone, the chat with the peer: is it muted, and until when? It was muted until {until.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.",
				ct
			);
		}
		finally
		{
			await client.UnmuteChatAsync(chat, CancellationToken.None);
		}
	}

	/// <summary>The chat with the peer marked unread with no message key: the owner says whether the phone shows it unread. It is marked read after.</summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true)]
	public async Task Live_MarkTheChatUnreadWithNoKey_TheOwnerSaysWhetherThePhoneShowsItUnread()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var (chat, _) = await peer.AccountAsync(ct);
		try
		{
			await client.MarkChatUnreadAsync(chat, ct);

			_ = await live.AskOwnerAsync("On the live account's phone, the chat list: is the chat with the peer marked unread (the unread dot)?", ct);
		}
		finally
		{
			await client.MarkChatReadAsync(chat, CancellationToken.None);
		}
	}

	/// <summary>
	/// The peer sends a text, so the chat has an unread message on the live account's phone; the chat is marked read
	/// with no message key, and the owner says whether the phone shows it read.
	/// </summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true)]
	public async Task Live_MarkTheChatReadWithNoKey_TheOwnerSaysWhetherThePhoneShowsItRead()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (account, _) = await live.AccountAsync(ct);
		var (chat, _) = await peer.AccountAsync(ct);
		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(client, id, ct);
		_ = await other.SendTextAsync(account, "wmn unread until marked read", id, cancellationToken: ct);
		_ = await LiveChecks.WithinAsync(arrived, "the peer's text");

		await client.MarkChatReadAsync(chat, ct);

		_ = await live.AskOwnerAsync("On the live account's phone, the chat list: the chat with the peer just got \"wmn unread until marked read\". Is it shown read now (no unread count)?", ct);
	}
}
