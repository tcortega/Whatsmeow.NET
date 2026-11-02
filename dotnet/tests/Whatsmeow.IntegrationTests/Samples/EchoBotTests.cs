using EchoBot;
using Microsoft.Extensions.Hosting;
using Whatsmeow.Events;
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
	private const string Status = "status@broadcast";
	private static readonly string[] s_sessions = ["alice", "bob"];

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_TheSample_PairsEachSessionByQrCode_AndEchoesOneTextPerSession()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		// After the sample's handler, so an event it recorded is one the sample's handler finished.
		_ = builder.Services.AddEchoBot().UseFakeWhatsApp().AddHandler<MessageReceived, RecordingHandler>();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var fake = await RegisteredAsync(host, ct);

		// One QR code at a time, on the console: the phone links each session once its code is shown.
		foreach (var session in s_sessions)
			await PairAsync(host, logs, session, ct);

		foreach (var session in s_sessions)
		{
			// A status, the account's own text from its phone, and another session's echo, are never echoed: a status
			// cannot be answered, and the others would loop.
			_ = await fake.DeliverMessageAsync(Text(session, "S1", "a status", chat: Status), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(FromThePhone(session, "M0", "sent from the phone"), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(session, "M1", "echo: from the other session"), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(session, "M2", $"hello {session}"), cancellationToken: ct);

			var echo = await NextMessageAsync(fake, session, after: 0, ct);
			Assert.Equal(Echo(echo, $"echo: hello {session}"), echo.Message);

			// The status runs in a chat of its own, in no order with the texts: once the sample's handler finished it,
			// the next message sent is a later text's echo, and the handler did not fail.
			await HandledAsync(Hosts.Handled(host), session, Status, ct);
			_ = await fake.DeliverMessageAsync(Text(session, "M3", $"again {session}"), cancellationToken: ct);
			var next = await NextMessageAsync(fake, session, echo.Index, ct);
			Assert.Equal(Echo(next, $"echo: again {session}"), next.Message);
		}

		Assert.Empty(logs.Named("HandlerFailed"));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pairing_TheFirstSessionsCodesRunOut_TheSecondStillPairsAndEchoes()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, binaries.NewDatabase()));
		_ = builder.Services.AddEchoBot().UseFakeWhatsApp();
		using var host = builder.Build();
		await host.StartAsync(ct);
		var fake = await RegisteredAsync(host, ct);

		_ = await logs.WaitForAsync(record => IsQrCodeOf(record, "alice"), ct);
		_ = await fake.ExpireQrCodesAsync(new ExpireQrCodesRequest { SessionId = "alice" }, cancellationToken: ct);
		await PairAsync(host, logs, "bob", ct);
		_ = await fake.DeliverMessageAsync(Text("bob", "M1", "hello bob"), cancellationToken: ct);

		var echo = await NextMessageAsync(fake, "bob", after: 0, ct);
		Assert.Equal(Echo(echo, "echo: hello bob"), echo.Message);
		// Alice is as before the pairing, for the next start to pair.
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(Hosts.Client(host, "alice"), ct));
		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Redelivery_OfATextEchoedBeforeTheHostStopped_IsNotEchoedAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var database = binaries.NewDatabase();

		// The holding handler, after the sample's, keeps M1 unfinished until the stop: so it is never acked.
		var first = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = first.Services.AddEchoBot().UseFakeWhatsApp().AddHandler<MessageReceived, HoldingHandler>();
		using (var firstHost = first.Build())
		{
			await firstHost.StartAsync(ct);
			var firstFake = await RegisteredAsync(firstHost, ct);
			await PairAsync(firstHost, logs, "alice", ct);
			_ = await firstFake.DeliverMessageAsync(Text("alice", "M1", "hello alice", id: "M1"), cancellationToken: ct);

			var echo = await NextMessageAsync(firstFake, "alice", after: 0, ct);
			Assert.Equal(Echo(echo, "echo: hello alice"), echo.Message);
			_ = await Hosts.Handled(firstHost).NextAsync(ct);
			await firstHost.StopAsync(ct);
		}

		// The next host gets M1 again and echoes it with the same id, which is never sent twice: the first message the
		// new sidecar sends is M2's echo.
		var second = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		_ = second.Services.AddEchoBot().UseFakeWhatsApp();
		using var secondHost = second.Build();
		await secondHost.StartAsync(ct);
		var secondFake = await RegisteredAsync(secondHost, ct);
		_ = await secondFake.DeliverMessageAsync(Text("alice", "M2", "again alice"), cancellationToken: ct);

		var next = await NextMessageAsync(secondFake, "alice", after: 0, ct);
		Assert.Equal(Echo(next, "echo: again alice"), next.Message);
		await secondHost.StopAsync(ct);
	}

	/// <summary>The fake of the host's sidecar, with the user who sends the texts on WhatsApp, whom a send reaches by the LID.</summary>
	private static async Task<FakeWhatsAppService.FakeWhatsAppServiceClient> RegisteredAsync(IHost host, CancellationToken ct)
	{
		var fake = Hosts.Fake(host);
		_ = await fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = Deliveries.Chat, Lid = Lid }, cancellationToken: ct).ConfigureAwait(false);
		return fake;
	}

	/// <summary>Links the session once the sample showed its QR code, and waits for it to be connected.</summary>
	private static async Task PairAsync(IHost host, LogCapture logs, string session, CancellationToken ct)
	{
		_ = await logs.WaitForAsync(record => IsQrCodeOf(record, session), ct).ConfigureAwait(false);
		_ = await Hosts.Fake(host).LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session) }, cancellationToken: ct).ConfigureAwait(false);
		var watch = StateWatch.Start(Hosts.Client(host, session), ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Pairing(), new SessionState.Connecting()).ConfigureAwait(false);
	}

	private static bool IsQrCodeOf(Microsoft.Extensions.Logging.Testing.FakeLogRecord record, string session) =>
		record.Id.Name == "ShowingQrCode" && record.GetStructuredStateValue("SessionId") == session;

	/// <summary>Waits until the recording handler got the session's message in <paramref name="chat"/>.</summary>
	private static async Task HandledAsync(Handled handled, string session, string chat, CancellationToken ct)
	{
		while (true)
		{
			var handling = await handled.NextAsync(ct).ConfigureAwait(false);
			if (handling.Event.SessionId == SessionId.From(session) && handling.Event.Source.Chat == Jid.Parse(chat))
				return;
		}
	}

	private static DeliverMessageRequest Text(string session, string name, string text, string chat = Deliveries.Chat, string? id = null)
	{
		var message = Deliveries.Message(session, id ?? $"{session}-{name}");
		message.Chat = chat;
		message.Text = text;
		return message;
	}

	/// <summary>A text the account sent from its phone to the chat, which reaches this device too.</summary>
	private static DeliverMessageRequest FromThePhone(string session, string name, string text)
	{
		var message = Text(session, name, text);
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

	/// <summary>The first message the session sent after the log entry <paramref name="after"/>; receipts and acks are not messages.</summary>
	private static async Task<Sent> NextMessageAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, ulong after, CancellationToken ct)
	{
		while (true)
		{
			var sent = (await fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			if (sent.KindCase == Sent.KindOneofCase.Message)
				return sent;
			after = sent.Index;
		}
	}
}
