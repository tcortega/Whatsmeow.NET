namespace Whatsmeow.Events;

/// <summary>What a message says. Each kind is a sealed record; <see cref="UnknownContent"/> is any kind this version does not map.</summary>
public abstract record MessageContent
{
	private protected MessageContent()
	{
	}
}
