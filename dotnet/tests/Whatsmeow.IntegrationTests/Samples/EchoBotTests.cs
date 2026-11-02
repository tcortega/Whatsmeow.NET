using EchoBot;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Hosting;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.IntegrationTests.Samples;

/// <summary>
/// The hosted sample, <c>dotnet/samples/EchoBot</c>, through its own setup (<see cref="EchoBotServices.AddEchoBot"/>) in
/// a generic host with a fake-mode sidecar of its own on a database of its own, as the hosting tests do: the host
/// starting the sidecar is part of the sample (ADR 0049). The fake WhatsApp is both phones; "nothing was echoed" shows
/// by order: the next message the session sent is the echo of a later one.
/// </summary>
public sealed class EchoBotTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;
	private const string Lid = "900000042@lid";
	private static readonly string[] s_sessions = ["alice", "bob"];

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_TheSample_PairsEachSessionByQrCode_AndEchoesOneTextPerSession()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddEchoBot().UseFakeWhatsApp();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var fake = Hosts.Fake(host);
		// The texts come from a user on WhatsApp, whom a send reaches by the LID.
		_ = await fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = Deliveries.Chat, Lid = Lid }, cancellationToken: ct);

		// One QR code at a time, on the console: the phone links each session once its code is shown.
		foreach (var session in s_sessions)
		{
			_ = await logs.WaitForAsync(record => record.Id.Name == "ShowingQrCode" && record.GetStructuredStateValue("SessionId") == session, ct);
			_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session) }, cancellationToken: ct);
			await using var watch = StateWatch.Start(Hosts.Client(host, session), ct);
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Pairing(), new SessionState.Connecting());
		}

		foreach (var session in s_sessions)
		{
			// The account's own text from its phone, and another session's echo, are never echoed: that would loop.
			_ = await fake.DeliverMessageAsync(FromThePhone(session, "M0", "sent from the phone"), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(session, "M1", "echo: from the other session"), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(session, "M2", $"hello {session}"), cancellationToken: ct);

			var echo = await NextMessageAsync(fake, session, ct);
			Assert.Equal(Echo(echo, $"echo: hello {session}"), echo.Message);
		}

		await host.StopAsync(ct);
	}

	private static DeliverMessageRequest Text(string session, string id, string text)
	{
		var message = Deliveries.Message(session, $"{session}-{id}");
		message.Text = text;
		return message;
	}

	/// <summary>A text the account sent from its phone to the chat, which reaches this device too.</summary>
	private static DeliverMessageRequest FromThePhone(string session, string id, string text)
	{
		var message = Text(session, id, text);
		message.IsFromMe = true;
		message.Sender = SessionSetup.Jid(session);
		return message;
	}

	/// <summary>The echo as WhatsApp got it, in the chat it answers, by its LID; its id and time are the sent message's own.</summary>
	private static SentMessage Echo(Sent sent, string text) =>
		new()
		{
			Id = sent.Message.Id,
			Chat = Lid,
			Message = new E2E.Message { Conversation = text },
			MessageTime = sent.Message.MessageTime,
		};

	/// <summary>The first message the session sent; receipts and acks are not messages.</summary>
	private static async Task<Sent> NextMessageAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, CancellationToken ct)
	{
		ulong after = 0;
		while (true)
		{
			var sent = (await fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			if (sent.KindCase == Sent.KindOneofCase.Message)
				return sent;
			after = sent.Index;
		}
	}
}
