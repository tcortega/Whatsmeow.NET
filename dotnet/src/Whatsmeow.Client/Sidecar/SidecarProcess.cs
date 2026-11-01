using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;

namespace Whatsmeow.Sidecar;

/// <summary>
/// One launch of the sidecar: spawn, token, tether, ready line, stderr forwarding, stop (ARCHITECTURE §7.8, §8.5).
/// </summary>
internal sealed class SidecarProcess
{
	private const int TailLines = 20;
	private static readonly TimeSpan s_stopGrace = TimeSpan.FromSeconds(5);
	private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

	private readonly Process _process;
	private readonly ILogger _sidecarLog;
	private readonly Lock _tailLock = new();
	private readonly Queue<string> _tail = new();
	private readonly Task _stderr;
	private Task _stdout = Task.CompletedTask;
	private int _stopped;

	private SidecarProcess(Process process, Task<int> exited, string token, ILogger sidecarLog)
	{
		_process = process;
		_sidecarLog = sidecarLog;
		Id = process.Id;
		Token = token;
		Exited = exited;
		_stderr = ForwardAsync(process.StandardError, stderr: true);
	}

	public int Id { get; }

	/// <summary>This launch's token, sent on every call.</summary>
	public string Token { get; }

	/// <summary>The socket path or pipe name from the ready line.</summary>
	public string Endpoint { get; private set; } = "";

	/// <summary>The version from the ready line.</summary>
	public string? Version { get; private set; }

	/// <summary>Completes with the exit code when the process exits.</summary>
	public Task<int> Exited { get; }

	/// <summary>
	/// Starts the sidecar and waits for its ready line. On any failure the process is killed and gone before this
	/// throws: <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> was cancelled,
	/// <see cref="SidecarUnavailableException"/> with the tail of stderr otherwise.
	/// </summary>
	/// <param name="database">The SQLite database file, passed with <c>--database</c>.</param>
	/// <param name="fakeWhatsApp">Pass <c>--fake-whatsapp</c>: only a test build accepts it (ADR 0019).</param>
	/// <param name="startup">Cancelled by the caller or by the startup timeout.</param>
	public static async Task<SidecarProcess> StartAsync(
		string binary,
		SidecarTransport transport,
		string? socketDirectory,
		string database,
		bool fakeWhatsApp,
		TimeSpan startupTimeout,
		ILogger clientLog,
		ILogger sidecarLog,
		CancellationToken startup,
		CancellationToken cancellationToken
	)
	{
		var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
		var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		var (process, exited) = Spawn(StartInfo(binary, transport, socketDirectory, database, fakeWhatsApp, nonce), clientLog);
		var sidecar = new SidecarProcess(process, exited, token, sidecarLog);

		string failure;
		Exception? cause = null;
		try
		{
			// The token is the first stdin line; stdin then stays open as the tether.
			await sidecar._process.StandardInput.WriteLineAsync(token.AsMemory(), startup).ConfigureAwait(false);
			await sidecar._process.StandardInput.FlushAsync(startup).ConfigureAwait(false);

			var line = await sidecar._process.StandardOutput.ReadLineAsync(startup).ConfigureAwait(false);
			var ready = line is null ? null : JsonSerializer.Deserialize(line, SidecarJsonContext.Default.ReadyLine);

			if (ready is { Event: "ready", Listen.Length: > 0 } && ready.Nonce == nonce)
			{
				sidecar.Endpoint = ready.Listen;
				sidecar.Version = ready.Version;
				// The sidecar prints nothing else on stdout; anything it does is logged, so the pipe never fills.
				sidecar._stdout = sidecar.ForwardAsync(sidecar._process.StandardOutput, stderr: false);
				return sidecar;
			}

			failure = ready switch
			{
				null => "it exited before its ready line",
				{ Event: "ready" } when ready.Nonce != nonce => "its ready line echoes another nonce, so it is not the process this client started",
				_ => $"its ready line is malformed: {line}",
			};
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await sidecar.KillAsync().ConfigureAwait(false);
			sidecar._process.Dispose();
			throw;
		}
		catch (OperationCanceledException e)
		{
			(failure, cause) = ($"it printed no ready line within {startupTimeout}", e);
		}
		catch (Exception e) when (e is IOException or JsonException)
		{
			(failure, cause) = ("it exited or wrote garbage before its ready line", e);
		}

		await sidecar.KillAsync().ConfigureAwait(false);
		var reason = $"{failure} (exit code {sidecar._process.ExitCode})";
		sidecar._process.Dispose();
		SidecarUnavailableException.ThrowStartFailed(reason, sidecar.Tail(), cause);
		return null;
	}

	/// <summary>
	/// The graceful stop: the Shutdown call, then EOF on stdin, then up to a 5 s grace for the exit, then the process
	/// tree is killed (ARCHITECTURE §8.5). The process is gone when this returns. Calling it again does nothing.
	/// </summary>
	public async Task StopAsync(Func<CancellationToken, Task> shutdownAsync, TimeProvider timeProvider)
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
			return;

		if (!_process.HasExited)
			await StopGracefullyAsync(shutdownAsync, timeProvider).ConfigureAwait(false);

		await KillAsync().ConfigureAwait(false);
		_process.Dispose();
	}

	private async Task StopGracefullyAsync(Func<CancellationToken, Task> shutdownAsync, TimeProvider timeProvider)
	{
		using var grace = new CancellationTokenSource(s_stopGrace, timeProvider);
		try
		{
			await shutdownAsync(grace.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is RpcException or OperationCanceledException)
		{
			// The sidecar may already be gone or stuck; closing stdin and the kill below still stop it.
		}

		try
		{
			_process.StandardInput.Close();
			await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is IOException or OperationCanceledException)
		{
			// Killed by the caller.
		}
	}

	/// <summary>Kills the process tree if it still runs, and waits until it and its stderr are done.</summary>
	public async Task KillAsync()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// It already exited.
		}

		await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
		await _stderr.ConfigureAwait(false);
		await _stdout.ConfigureAwait(false);
	}

	private static ProcessStartInfo StartInfo(string binary, SidecarTransport transport, string? socketDirectory, string database, bool fakeWhatsApp, string nonce)
	{
		var info = new ProcessStartInfo(binary)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardInputEncoding = s_utf8,
			StandardOutputEncoding = s_utf8,
			StandardErrorEncoding = s_utf8,
		};

		info.ArgumentList.Add("--database");
		info.ArgumentList.Add(database);
		info.ArgumentList.Add("--transport");
		info.ArgumentList.Add(transport is SidecarTransport.NamedPipe ? "pipe" : "unix");
		if (socketDirectory is not null)
		{
			info.ArgumentList.Add("--socket-dir");
			info.ArgumentList.Add(socketDirectory);
		}

		if (fakeWhatsApp)
			info.ArgumentList.Add("--fake-whatsapp");

		info.ArgumentList.Add("--nonce");
		info.ArgumentList.Add(nonce);
		return info;
	}

	/// <summary>
	/// Starts the process. On Unix the binary is made executable first; when it still cannot run (it cannot be
	/// chmodded, or it is on a <c>noexec</c> mount), a cached copy runs instead (ARCHITECTURE §8.5).
	/// </summary>
	private static (Process Process, Task<int> Exited) Spawn(ProcessStartInfo info, ILogger clientLog)
	{
		if (OperatingSystem.IsWindows())
			return StartOrThrow(info);

		_ = SidecarBinary.TryMakeExecutable(info.FileName);
		try
		{
			return StartOrThrow(info);
		}
		catch (SidecarUnavailableException inPlace)
		{
			var original = info.FileName;
			try
			{
				info.FileName = SidecarBinary.CopyToCache(original);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				SidecarUnavailableException.ThrowStartFailed($"{original} cannot run in place, and copying it to the cache failed ({e.Message})", [], inPlace);
			}

			SidecarLog.SidecarCopied(clientLog, original, info.FileName);
			return StartOrThrow(info);
		}
	}

	/// <summary>Starts the process; its exit code is captured when it exits, so it never depends on a later read.</summary>
	private static (Process Process, Task<int> Exited) StartOrThrow(ProcessStartInfo info)
	{
		var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);
		try
		{
			_ = process.Start();
			return (process, exited.Task);
		}
		catch (Win32Exception e)
		{
			process.Dispose();
			SidecarUnavailableException.ThrowStartFailed($"{info.FileName} cannot be executed ({e.Message})", [], e);
			return default;
		}
	}

	/// <summary>Logs every line of one of the sidecar's pipes under Whatsmeow.Sidecar; stderr lines also go to the tail.</summary>
	private async Task ForwardAsync(StreamReader pipe, bool stderr)
	{
		try
		{
			while (await pipe.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				if (stderr)
				{
					lock (_tailLock)
					{
						if (_tail.Count == TailLines)
							_ = _tail.Dequeue();
						_tail.Enqueue(line);
					}
				}

				var level = stderr ? LevelOf(line) : LogLevel.Information;
				SidecarLog.SidecarOutput(_sidecarLog, level, line);
			}
		}
		catch (Exception e) when (e is IOException or ObjectDisposedException)
		{
			// The pipe broke with the process; nothing more to forward.
		}
	}

	/// <summary>The last lines the sidecar wrote to stderr.</summary>
	public string[] Tail()
	{
		lock (_tailLock)
			return [.. _tail];
	}

	/// <summary>The level of a zerolog JSON line; a line that is not one is Information.</summary>
	private static LogLevel LevelOf(string line)
	{
		try
		{
			return JsonSerializer.Deserialize(line, SidecarJsonContext.Default.SidecarLogLine)?.Level switch
			{
				"trace" => LogLevel.Trace,
				"debug" => LogLevel.Debug,
				"warn" => LogLevel.Warning,
				"error" => LogLevel.Error,
				"fatal" or "panic" => LogLevel.Critical,
				_ => LogLevel.Information,
			};
		}
		catch (JsonException)
		{
			return LogLevel.Information;
		}
	}
}
