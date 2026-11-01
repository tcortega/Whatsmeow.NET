using System.Buffers;
using Vogen;

// One set of defaults for every id (docs/guidelines/csharp.md IDs, ADR 0020): validation failures are our own
// exception; no converter and no converter factory are generated (Vogen 8.0.7 generates both as public types with
// nullable annotations off, which fails PublicApiAnalyzers' RS0041, the factory even with Conversions.None); and
// TryFrom returns a bool, so no Vogen type is in our API.
[assembly: VogenDefaults(
	throws: typeof(Whatsmeow.WhatsmeowValidationException),
	conversions: Conversions.None,
	tryFromGeneration: TryFromGeneration.GenerateBoolMethod,
	systemTextJsonConverterFactoryGeneration: SystemTextJsonConverterFactoryGeneration.Omit
)]

namespace Whatsmeow;

/// <summary>
/// A session's id, chosen by the application: 1 to 64 characters of <c>A-Z a-z 0-9 . _ -</c>, starting with a letter
/// or a digit, case-sensitive. <see cref="From(string)"/> throws <see cref="WhatsmeowValidationException"/> for any
/// other value.
/// </summary>
[ValueObject<string>]
public readonly partial struct SessionId
{
	private const int MaxLength = 64;

	private static readonly SearchValues<char> s_allowed = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

	private static Validation Validate(string value) =>
		value is { Length: >= 1 and <= MaxLength } && char.IsAsciiLetterOrDigit(value[0]) && !value.AsSpan().ContainsAnyExcept(s_allowed)
			? Validation.Ok
			: Validation.Invalid($"\"{value}\" is not a session id: 1 to {MaxLength} of A-Z a-z 0-9 . _ -, starting with a letter or a digit.");
}
