using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Whatsmeow.Events;
using Whatsmeow.Messaging;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// The records a received message must be. The fake-mode messaging tests and <c>Whatsmeow.LiveTests</c> build what they
/// expect with these same functions and compare it whole (docs/guidelines/tests.md Live WhatsApp). The fields WhatsApp
/// sets on the way, the source, the push name and the time, are passed in: the fake's are fixed, and a live test reads
/// them off the message after checking that its source names the sender.
/// </summary>
internal static class MessageChecks
{
	/// <summary>A message with <paramref name="content"/>, and no quote, mentions or flags.</summary>
	public static MessageReceived Received(SessionId session, MessageId id, MessageSource source, string? pushName, DateTimeOffset timestamp, MessageContent content) =>
		new()
		{
			SessionId = session,
			Id = id,
			Source = source,
			PushName = pushName,
			Timestamp = timestamp,
			Content = content,
		};

	/// <summary>A decrypted vote on <paramref name="poll"/> that selected <paramref name="options"/>, none to clear it.</summary>
	public static PollVoteContent Vote(MessageKey poll, params string[] options) => new() { Poll = poll, SelectedOptionHashes = [.. options.Select(Hash)] };

	/// <summary>An option's hash, as WhatsApp carries it in a vote: SHA-256 of its UTF-8 text.</summary>
	public static ImmutableArray<byte> Hash(string option) => [.. SHA256.HashData(Encoding.UTF8.GetBytes(option))];
}
