using System.Collections.Concurrent;
using System.Text;
using EchoConsole;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.IntegrationTests.Samples;

/// <summary>
/// The sample without DI, <c>dotnet/samples/EchoConsole</c>, through its own setup (<see cref="Echo.StartAsync"/>) on
/// the shared fake-mode sidecar: the README's quick start without DI is that sample's code (ADR 0054). The fake
/// WhatsApp is the phone; "nothing was echoed" shows by order: the next message the session sent is a later text's echo.
/// The redelivery test starts fake-mode sidecars of its own on a database of its own, because stopping the program is
/// the behaviour under test.
/// </summary>
public sealed class EchoConsoleTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;
	// The user who texts the sample, its own on the shared fake: another test registers Deliveries.Chat with another LID.
	private const string Phone = "5511900000043@s.whatsapp.net";
	private const string Lid = "900000043@lid";
	private const string Status = "status@broadcast";

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_TheSample_PairsByQrCode_AndEchoesEachTextOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var fake = fixture.Fake;
		var client = await RegisteredAsync(fixture.Sidecar, fake, "echo-console", ct);
		var errors = new ConcurrentQueue<HandlerError>();
		using var failed = client.OnHandlerError(errors.Enqueue);
		using var console = new ConsoleCapture();

		using (await StartPairingAsync(client, fake, console, ct))
		{
			// After the sample's handler, so a message it recorded is one the sample's handler finished.
			using var handled = new Received<MessageReceived>(client);

			// A status cannot be answered, and the account's own text and an echo would loop: none is echoed, so the
			// next message sent is the later text's echo.
			_ = await fake.DeliverMessageAsync(Text(client, "S1", "a status", chat: Status), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(client, "M1", "echo: from another echo"), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(client, "M2", "hello", isFromMe: true), cancellationToken: ct);
			_ = await fake.DeliverMessageAsync(Text(client, "M3", "hello console"), cancellationToken: ct);

			var echo = await NextMessageAsync(fake, client, after: 0, ct);
			Assert.Equal(new E2E.Message { Conversation = "echo: hello console" }, echo.Message.Message);
			Assert.Equal(Lid, echo.Message.Chat);

			// The status runs in a chat of its own, in no order with the texts: once the sample's handler finished it,
			// it had not failed.
			while ((await handled.NextAsync(ct)).Source.Chat != Jid.Parse(Status))
				continue;
			Assert.Empty(errors);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ThePairedSessionIsDisconnected_ConnectsIt_AndEchoes()
	{
		var ct = TestContext.Current.CancellationToken;
		var fake = fixture.Fake;
		var client = await RegisteredAsync(fixture.Sidecar, fake, "echo-console-again", ct);
		using (var pairing = new ConsoleCapture())
		using (await StartPairingAsync(client, fake, pairing, ct))
			await client.DisconnectAsync(ct);

		// The next run of the program: the session is paired and Disconnected, so the sample connects it.
		using var console = new ConsoleCapture();
		using (await Echo.StartAsync(client, console, ct))
		{
			Assert.Empty(console.Text);
			_ = await fake.DeliverMessageAsync(Text(client, "M1", "hello again"), cancellationToken: ct);

			var echo = await NextMessageAsync(fake, client, after: 0, ct);
			Assert.Equal(new E2E.Message { Conversation = "echo: hello again" }, echo.Message.Message);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ThePairingFails_StopsEchoing()
	{
		var ct = TestContext.Current.CancellationToken;
		var fake = fixture.Fake;
		var client = await RegisteredAsync(fixture.Sidecar, fake, "echo-console-unpaired", ct);
		using var console = new ConsoleCapture();

		// The phone never scans the QR code the sample shows.
		var expiring = ExpireOnceShownAsync();
		var timedOut = await Assert.ThrowsAsync<PairingException>(() => Echo.StartAsync(client, console, ct));
		await expiring;
		Assert.Equal(WhatsmeowErrorCode.PairingTimedOut, timedOut.ErrorCode);

		// Paired another way, the session gets the text, and the sample's handler is gone: the next message sent is the
		// test's own, not an echo.
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = client.SessionId.Value, Jid = SessionSetup.Jid(client.SessionId.Value) }, cancellationToken: ct);
		using var handled = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);
		_ = await fake.DeliverMessageAsync(Text(client, "M1", "hello"), cancellationToken: ct);
		_ = await handled.NextAsync(ct);
		_ = await client.SendTextAsync(Jid.Parse(Phone), "from the test", cancellationToken: ct);

		var sent = await NextMessageAsync(fake, client, after: 0, ct);
		Assert.Equal(new E2E.Message { Conversation = "from the test" }, sent.Message.Message);

		async Task ExpireOnceShownAsync()
		{
			await console.WrittenAsync(ct).ConfigureAwait(false);
			_ = await fake.ExpireQrCodesAsync(new ExpireQrCodesRequest { SessionId = client.SessionId.Value }, cancellationToken: ct).ConfigureAwait(false);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Redelivery_OfATextEchoedBeforeTheProgramStopped_IsNotEchoedAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "echo-console-redelivery";
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };

		await using (var first = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
			var client = await RegisteredAsync(first, fake, Session, ct);
			using var console = new ConsoleCapture();
			using (await StartPairingAsync(client, fake, console, ct))
			{
				// After the sample's handler, it holds M1 unfinished until the stop: so M1 is never acked.
				var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				using var holding = new Received<MessageReceived>(
					client,
					async (_, token) =>
					{
						held.SetResult();
						await new TaskCompletionSource().Task.WaitAsync(token).ConfigureAwait(false);
					}
				);
				_ = await fake.DeliverMessageAsync(Text(client, "M1", "hello"), cancellationToken: ct);

				var echo = await NextMessageAsync(fake, client, after: 0, ct);
				Assert.Equal(new E2E.Message { Conversation = "echo: hello" }, echo.Message.Message);
				await held.Task.WaitAsync(ct);
			}
		}

		// The next run gets M1 again and echoes it with the same id, which is never sent twice: the first message the new
		// sidecar sends is M2's echo.
		await using var second = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var secondFake = new FakeWhatsAppService.FakeWhatsAppServiceClient(second.CallInvoker);
		var again = await RegisteredAsync(second, secondFake, Session, ct);
		using var secondConsole = new ConsoleCapture();
		using (await Echo.StartAsync(again, secondConsole, ct))
		{
			_ = await secondFake.DeliverMessageAsync(Text(again, "M2", "hello again"), cancellationToken: ct);

			var next = await NextMessageAsync(secondFake, again, after: 0, ct);
			Assert.Equal(new E2E.Message { Conversation = "echo: hello again" }, next.Message.Message);
		}
	}

	private static async Task<IWhatsAppClient> RegisteredAsync(WhatsmeowSidecar sidecar, FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, CancellationToken ct)
	{
		_ = await fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = Phone, Lid = Lid }, cancellationToken: ct).ConfigureAwait(false);
		return await sidecar.Sessions.GetOrCreateAsync(SessionId.From(session), ct).ConfigureAwait(false);
	}

	/// <summary>Starts the sample on a new session, links it once the sample drew its QR code, and waits for the start to end.</summary>
	private static async Task<IDisposable> StartPairingAsync(IWhatsAppClient client, FakeWhatsAppService.FakeWhatsAppServiceClient fake, ConsoleCapture console, CancellationToken ct)
	{
		var starting = Echo.StartAsync(client, console, ct);
		await console.WrittenAsync(ct).ConfigureAwait(false);
		var session = client.SessionId.Value;
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = session, Jid = SessionSetup.Jid(session) }, cancellationToken: ct).ConfigureAwait(false);
		var echo = await starting.ConfigureAwait(false);
		Assert.Equal(new SessionState.Connected(), await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
		return echo;
	}

	private static DeliverMessageRequest Text(IWhatsAppClient client, string name, string text, bool isFromMe = false, string chat = Phone)
	{
		var session = client.SessionId.Value;
		var message = Deliveries.Message(session, $"{session}-{name}", Phone);
		message.Chat = chat;
		message.Text = text;
		message.IsFromMe = isFromMe;
		if (isFromMe)
			message.Sender = SessionSetup.Jid(session);
		return message;
	}

	/// <summary>The first message the session sent after the log entry <paramref name="after"/>; receipts and acks are not messages.</summary>
	private static async Task<Sent> NextMessageAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, IWhatsAppClient client, ulong after, CancellationToken ct)
	{
		while (true)
		{
			var sent = (await fake.WaitForSentAsync(new WaitForSentRequest { SessionId = client.SessionId.Value, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
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
			WriteLine(value);
			_ = _written.TrySetResult();
			return Task.CompletedTask;
		}

		// What the sample's WriteLineAsync(string, CancellationToken) reaches [observed: .NET 11.0.0, a run without this override].
		public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
		{
			WriteLine(buffer.Span);
			_ = _written.TrySetResult();
			return Task.CompletedTask;
		}
	}
}
