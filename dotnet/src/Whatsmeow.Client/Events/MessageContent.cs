namespace Whatsmeow.Events;

/// <summary>What a message says. Each kind is a sealed record; <see cref="UnknownContent"/> is any kind this version does not map.</summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record MessageContent
{
	private protected MessageContent()
	{
	}
}
