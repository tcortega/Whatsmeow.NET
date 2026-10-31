using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Spike.V1;

var sidecar = args[0];
var transport = args[1];
var seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
var failures = 0;
void Report(string name, bool ok, string detail)
{
	Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [{transport}] {name}: {detail}");
	if (!ok)
		failures++;
}

var psi = new ProcessStartInfo(sidecar, $"--transport {transport}") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
using var proc = Process.Start(psi)!;
_ = Task.Run(async () => { while (await proc.StandardError.ReadLineAsync() is { } l) Console.Error.WriteLine("sidecar: " + l); });
await proc.StandardInput.WriteLineAsync("token");
await proc.StandardInput.FlushAsync();
using var ready = JsonDocument.Parse((await proc.StandardOutput.ReadLineAsync())!);
var addr = ready.RootElement.GetProperty("listen").GetString()!;
Console.WriteLine($"INFO [{transport}] sidecar {ready.RootElement.GetProperty("go").GetString()}, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, listen {addr}");

var handler = new SocketsHttpHandler
{
	ConnectCallback = transport == "uds"
		? async (_, ct) =>
		{
			var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			try
			{
				await s.ConnectAsync(new UnixDomainSocketEndPoint(addr), ct);
				return new NetworkStream(s, ownsSocket: true);
			}
			catch
			{
				s.Dispose();
				throw;
			}
		}
		: async (_, ct) =>
		{
			var pipe = new NamedPipeClientStream(".", addr[@"\\.\pipe\".Length..], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			try
			{
				await pipe.ConnectAsync(ct);
				return pipe;
			}
			catch
			{
				await pipe.DisposeAsync();
				throw;
			}
		},
};
using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = 16 << 20, MaxSendMessageSize = 16 << 20 });
var client = new EventService.EventServiceClient(channel);

async Task Guard(string name, Func<Task<(bool ok, string detail)>> body)
{
	try
	{
		var (ok, detail) = await body();
		Report(name, ok, detail);
	}
	catch (Exception ex)
	{
		Report(name, false, ex.GetType().Name + ": " + ex.Message);
	}
}

await Guard("unary ping", async () => ((await client.PingAsync(new PingRequest())).Nonce == "n0nce", "ok"));

async Task<(bool, string)> ServerStream(uint payload, uint perRound, int budgetSeconds)
{
	var sw = Stopwatch.StartNew();
	ulong total = 0;
	var rounds = 0;
	while (sw.Elapsed.TotalSeconds < budgetSeconds)
	{
		ulong expect = 1;
		using var call = client.StreamEvents(new StreamEventsRequest { AfterSeq = 0, Count = perRound, PayloadSize = payload });
		await foreach (var ev in call.ResponseStream.ReadAllAsync())
		{
			if (ev.Seq != expect || ev.Payload.Length != payload)
				return (false, $"corrupt or out of order at round {rounds} seq {ev.Seq} (want {expect}), payload {ev.Payload.Length}");
			expect++;
		}
		if (expect - 1 != perRound)
			return (false, $"short stream: {expect - 1}/{perRound}");
		total += perRound;
		rounds++;
	}
	return (true, $"{total:N0} events in {rounds} rounds, {total / sw.Elapsed.TotalSeconds:N0}/s, {total * payload / sw.Elapsed.TotalSeconds / 1048576:N0} MiB/s");
}

await Guard("server stream 512 B", () => ServerStream(512, 1_000_000, seconds));
await Guard("server stream 64 KiB (multi-frame messages)", () => ServerStream(65536, 20_000, seconds / 2));

await Guard("client stream upload 64 KiB chunks", async () =>
{
	var sw = Stopwatch.StartNew();
	using var call = client.Upload();
	var data = ByteString.CopyFrom(new byte[65536]);
	ulong i = 0;
	while (sw.Elapsed.TotalSeconds < seconds / 2)
		await call.RequestStream.WriteAsync(new Chunk { Index = i++, Data = data });
	await call.RequestStream.CompleteAsync();
	var r = await call;
	return (r.InOrder && r.Chunks == i && r.Bytes == i * 65536, $"{r.Chunks:N0} chunks, {r.Bytes / 1048576.0 / sw.Elapsed.TotalSeconds:N0} MiB/s, in order {r.InOrder}");
});

await Guard("bidi 500 B back to back (#7039 shape) + concurrent pings", async () =>
{
	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
	using var call = client.Chat(cancellationToken: CancellationToken.None);
	var payload = ByteString.CopyFrom(new byte[500]);
	ulong sent = 0, recv = 0;
	var pings = 0;
	var sender = Task.Run(async () =>
	{
		while (!cts.IsCancellationRequested)
			await call.RequestStream.WriteAsync(new Event { Seq = sent++, Payload = payload });
		await call.RequestStream.CompleteAsync();
	});
	var pinger = Task.Run(async () =>
	{
		while (!cts.IsCancellationRequested)
		{
			await client.PingAsync(new PingRequest());
			pings++;
			await Task.Delay(50);
		}
	});
	await foreach (var e in call.ResponseStream.ReadAllAsync())
	{
		if (e.Seq != recv)
			return (false, $"out of order: got {e.Seq} want {recv}");
		recv++;
	}
	await sender;
	await pinger;
	return (recv == sent, $"{recv:N0} echoed in order over {seconds}s, {pings} pings during the stream");
});

proc.StandardInput.Close();
var exited = proc.WaitForExit(10_000);
Report("stdin EOF stops sidecar", exited, exited ? $"exit {proc.ExitCode}" : "still running");
if (!exited)
	proc.Kill(entireProcessTree: true);
Console.WriteLine(failures == 0 ? $"ALL PASS [{transport}]" : $"{failures} FAILED [{transport}]");
return failures;
