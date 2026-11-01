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
	private static Validation Validate(string value) => string.IsNullOrEmpty(value) ? Validation.Invalid("A message id must not be empty.") : Validation.Ok;
}
