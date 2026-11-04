using System.Text;
using EchoConsole;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.IntegrationTests.Samples;

/// <summary>
/// The sample without DI, <c>dotnet/samples/EchoConsole</c>, through its own setup (<see cref="Echo.StartAsync"/>) on
/// the shared fake-mode sidecar: the README's quick start without DI is that sample's code (ADR 0054). The fake
/// WhatsApp is the phone; "nothing was echoed" shows by order: the next message the session sent is a later text's echo.
/// </summary>
public sealed class EchoConsoleTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	// The user who texts the sample, its own on the shared fake: another test registers Deliveries.Chat with another LID.
	private const string Phone = "5511900000043@s.whatsapp.net";
	private const string Lid = "900000043@lid";

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_TheSample_PairsByQrCode_AndEchoesEachTextOnce_WithNoConnect()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await RegisteredAsync("echo-console", ct);
		using var console = new ConsoleCapture();

		using (await StartPairingAsync(client, console, ct))
		{
			// The account's own text and an echo are never echoed: the next message sent is the later text's echo.
			_ = await fixture.Fake.DeliverMessageAsync(Text(client, "M1", "echo: from another echo"), cancellationToken: ct);
			_ = await fixture.Fake.DeliverMessageAsync(Text(client, "M2", "hello", isFromMe: true), cancellationToken: ct);
			_ = await fixture.Fake.DeliverMessageAsync(Text(client, "M3", "hello console"), cancellationToken: ct);

			var echo = await NextMessageAsync(client, after: 0, ct);
			Assert.Equal(new E2E.Message { Conversation = "echo: hello console" }, echo.Message.Message);
			Assert.Equal(Lid, echo.Message.Chat);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ThePairedSessionIsDisconnected_ConnectsIt_AndEchoes()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await RegisteredAsync("echo-console-again", ct);
		using (var pairing = new ConsoleCapture())
		using (await StartPairingAsync(client, pairing, ct))
			await client.DisconnectAsync(ct);

		// The next run of the program: the session is paired and Disconnected, so the sample connects it.
		using var console = new ConsoleCapture();
		using (await Echo.StartAsync(client, console, ct))
		{
			Assert.Empty(console.Text);
			_ = await fixture.Fake.DeliverMessageAsync(Text(client, "M1", "hello again"), cancellationToken: ct);

			var echo = await NextMessageAsync(client, after: 0, ct);
			Assert.Equal(new E2E.Message { Conversation = "echo: hello again" }, echo.Message.Message);
		}
	}

	private async Task<IWhatsAppClient> RegisteredAsync(string session, CancellationToken ct)
	{
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = Phone, Lid = Lid }, cancellationToken: ct).ConfigureAwait(false);
		return await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(session), ct).ConfigureAwait(false);
	}

	/// <summary>Starts the sample on a new session, links it once the sample drew its QR code, and waits for the start to end.</summary>
	private async Task<IDisposable> StartPairingAsync(IWhatsAppClient client, ConsoleCapture console, CancellationToken ct)
	{
		var starting = Echo.StartAsync(client, console, ct);
		await console.WrittenAsync(ct).ConfigureAwait(false);
		var session = client.SessionId.Value;
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session) }, cancellationToken: ct).ConfigureAwait(false);
		var echo = await starting.ConfigureAwait(false);
		Assert.Equal(new SessionState.Connected(), await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
		return echo;
	}

	private static DeliverMessageRequest Text(IWhatsAppClient client, string name, string text, bool isFromMe = false)
	{
		var session = client.SessionId.Value;
		var message = Deliveries.Message(session, $"{session}-{name}", Phone);
		message.Text = text;
		message.IsFromMe = isFromMe;
		if (isFromMe)
			message.Sender = SessionSetup.Jid(session);
		return message;
	}

	/// <summary>The first message the session sent after the log entry <paramref name="after"/>; receipts and acks are not messages.</summary>
	private async Task<Sent> NextMessageAsync(IWhatsAppClient client, ulong after, CancellationToken ct)
	{
		while (true)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = client.SessionId.Value, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			if (sent.KindCase == Sent.KindOneofCase.Message)
				return sent;
			after = sent.Index;
		}
	}

	/// <summary>The sample's console: what it wrote, and a wait for its first write, the QR code.</summary>
	private sealed class ConsoleCapture : StringWriter
	{
		private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConsoleCapture()
			: base(System.Globalization.CultureInfo.InvariantCulture) { }

		public string Text => ToString();

		public override Encoding Encoding => Encoding.UTF8;

		public Task WrittenAsync(CancellationToken cancellationToken) => _written.Task.WaitAsync(cancellationToken);

		public override void Write(char value)
		{
			base.Write(value);
			_ = _written.TrySetResult();
		}

		public override void Write(string? value)
		{
			base.Write(value);
			_ = _written.TrySetResult();
		}

		public override Task WriteLineAsync(string? value)
		{
			base.WriteLine(value);
			_ = _written.TrySetResult();
			return Task.CompletedTask;
		}
	}
}
