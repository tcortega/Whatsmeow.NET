using System.Collections.Concurrent;
using System.Security.Cryptography;
using Whatsmeow.Events;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Messaging (#9) and the account's own messages against real WhatsApp (ADR 0025 "Live, for #10"). Each result goes to
/// <c>sidecar.log</c> and the test output, for ARCHITECTURE §14; the owner checks the phone (the checklist in
/// docs/guidelines/tests.md Live WhatsApp).
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveMessagingTests(LiveSidecar live)
{
	/// <summary>How long a test watches for receipts and echoes of what it sent.</summary>
	private static readonly TimeSpan s_watch = TimeSpan.FromSeconds(60);

	/// <summary>The phone's own message to its own chat reaches the companion as the account's, IsFromMe.</summary>
	[LiveFact]
	public async Task Live_AMessageThePhoneSendsToItself_ReachesTheCompanionAsFromMe()
	{
		var ct = TestContext.Current.CancellationToken;
		var ping = await live.PingAsync(ct);

		Assert.True(ping.Source.IsFromMe);
		Assert.Equal(ping.Source.Chat.User, ping.Source.Sender.User);
		live.Write(
			$"live: the phone's ping came as {ping.Source.Chat.Server} chat, from device {ping.Source.Sender.Device}, "
				+ $"its id {ping.Id.Value.Length} characters starting {ping.Id.Value[..Math.Min(4, ping.Id.Value.Length)]}"
		);
	}

	/// <summary>
	/// A text to the account's own chat: where it went, its timestamp, a repeat with the same id gives the same result
	/// (the owner sees one message), the receipts the account's own devices send, and no event on this companion,
	/// which whatsmeow does not encrypt for (send.go:1314-1316) [observed: source].
	/// </summary>
	[LiveFact]
	public async Task Live_SendTextToSelf_ARepeatGivesTheSameResult_AndTheReceiptsAreRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var account = (await live.PingAsync(ct)).Source.Chat;
		var client = await live.LiveAsync(ct);
		var id = MessageId.New();
		var echoes = new ConcurrentQueue<MessageReceived>();
		var receipts = new ConcurrentQueue<ReceiptReceived>();
		using var messages = client.On<MessageReceived>(
			(e, _) =>
			{
				if (e.Id == id)
					echoes.Enqueue(e);
				return ValueTask.CompletedTask;
			}
		);
		using var receiptWatch = client.On<ReceiptReceived>(
			(e, _) =>
			{
				if (e.MessageIds.Contains(id))
					receipts.Enqueue(e);
				return ValueTask.CompletedTask;
			}
		);

		var first = await client.SendTextAsync(account, $"wmn self {id} (one message expected)", id, ct);
		var repeat = await client.SendTextAsync(account, $"wmn self {id} (one message expected)", id, ct);
		Assert.Equal(first, repeat);
		live.Write($"live: sent to self as {first.Chat} (the ping's chat {account}), at {first.Timestamp:O}");

		await Task.Delay(s_watch, LiveClock.Time, ct);
		live.Write($"live: {receipts.Count} receipts in {s_watch.TotalSeconds:F0} s: {string.Join(", ", receipts.Select(r => $"{r.Type} from {r.Source.Sender}"))}");
		Assert.Empty(echoes);
	}

	/// <summary>
	/// One text whose id is in the format the phone made the ping's, not whatsmeow's 3EB0 (ADR 0025): whether WhatsApp
	/// takes it is recorded either way; the live build's id rule lets it through (ADR 0032).
	/// </summary>
	[LiveFact]
	public async Task Live_SendTextWithAnIdInThePhonesFormat_TheAnswerIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var ping = await live.PingAsync(ct);
		var client = await live.LiveAsync(ct);
		var id = PhoneStyle(ping.Id);

		try
		{
			var result = await client.SendTextAsync(ping.Source.Chat, $"wmn phone-style id {id}", id, ct);
			Assert.Equal(id, result.Id);
			live.Write($"live: WhatsApp took the phone-style id {id} ({id.Value.Length} characters) at {result.Timestamp:O}");
		}
		catch (WhatsmeowException e)
		{
			live.Write($"live: WhatsApp refused the phone-style id {id}: {e.ErrorCode} {e.Message}");
		}
	}

	/// <summary>A new id like <paramref name="sample"/>: the same length, its first two characters, then random hex in its case.</summary>
	private static MessageId PhoneStyle(MessageId sample)
	{
		var value = sample.Value;
		Assert.True(value.Length > 4 && value.All(char.IsAsciiLetterOrDigit), $"the ping's id is not letters and digits: {value}");
		var hex = RandomNumberGenerator.GetHexString(value.Length - 2, lowercase: value.Any(char.IsAsciiLetterLower));
		var id = value[..2] + hex;
		Assert.False(id.StartsWith("3EB0", StringComparison.Ordinal), $"the ping's id is in whatsmeow's format: {value}");
		return MessageId.From(id);
	}
}
