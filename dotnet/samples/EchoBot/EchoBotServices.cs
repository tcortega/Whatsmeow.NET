using Microsoft.Extensions.DependencyInjection;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.History;

namespace EchoBot;

/// <summary>The echo bot's setup: one sidecar for the host, two sessions, their handlers, and pairing on the console.</summary>
public static class EchoBotServices
{
	/// <summary>The first session's id, which is also its service key.</summary>
	public const string Alice = "alice";

	/// <summary>The second session's id, which is also its service key.</summary>
	public const string Bob = "bob";

	public static WhatsmeowBuilder AddEchoBot(this IServiceCollection services)
	{
		var whatsmeow = services
			.AddWhatsmeow()
			.AddSession(Alice)
			.AddSession(Bob)
			.AddHandler<MessageReceived, EchoHandler>()
			.AddHandler<ConnectionStateChanged, StateLog>()
			.AddHandler<HistoryReceived, HistoryLog>();
		_ = services.AddHostedService<QrPairing>();
		return whatsmeow;
	}
}
