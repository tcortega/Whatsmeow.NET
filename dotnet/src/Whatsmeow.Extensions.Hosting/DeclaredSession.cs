namespace Whatsmeow.Extensions.Hosting;

/// <summary>A session <see cref="WhatsmeowBuilder.AddSession"/> declared.</summary>
internal sealed class DeclaredSession(SessionId id)
{
	public SessionId Id { get; } = id;
}
