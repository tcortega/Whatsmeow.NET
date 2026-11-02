using System.Security.Cryptography;
using Vogen;

namespace Whatsmeow;

/// <summary>
/// A WhatsApp message id. With the <see cref="SessionId"/> it identifies a message for deduplication: a redelivered
/// event carries the same pair (ARCHITECTURE §8.4). <see cref="From(string)"/> throws
/// <see cref="WhatsmeowValidationException"/> for an empty value.
/// </summary>
[ValueObject<string>]
public readonly partial struct MessageId
{
	/// <summary>
	/// A new random id for a message to send, in the only format a send accepts: <c>3EB0</c> and 18 uppercase hex
	/// digits, the format whatsmeow makes. Keep it to send the message again safely: a repeat with the same id is never
	/// sent twice.
	/// </summary>
	public static MessageId New() => From("3EB0" + RandomNumberGenerator.GetHexString(18));

	private static Validation Validate(string value) => string.IsNullOrEmpty(value) ? Validation.Invalid("A message id must not be empty.") : Validation.Ok;
}
