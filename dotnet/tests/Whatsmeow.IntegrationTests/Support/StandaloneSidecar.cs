using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// A sidecar the test runs standalone, as an orchestrator would (ADR 0046): <c>--listen</c> and <c>--token-file</c>,
/// stdin at EOF, no parent tether. The test owns the process and kills it, never by name.
/// </summary>
public sealed class StandaloneSidecar : IAsyncDisposable
{
	private readonly Process _process;
	private readonly StringBuilder _stderr = new();
	private readonly Task _forwarding;

	private StandaloneSidecar(Process process, string listen)
	{
		_process = process;
		Listen = listen;
		_forwarding = ForwardAsync();
	}

	/// <summary>Its ready line's endpoint: the socket path, or the bound TCP address.</summary>
	public string Listen { get; }

	/// <summary>The address a client configures: <c>unix:&lt;path&gt;</c> or <c>tcp:&lt;host&gt;:&lt;port&gt;</c>.</summary>
	public string Address => Path.IsPathFullyQualified(Listen) ? "unix:" + Listen : "tcp:" + Listen;

	public int Id => _process.Id;

	public bool HasExited => _process.HasExited;

	/// <summary>Everything it wrote to stderr so far.</summary>
	public string Stderr
	{
		get
		{
			lock (_stderr)
				return _stderr.ToString();
		}
	}

	/// <summary>A new token file with a new random token, readable by the current user only.</summary>
	public static string NewTokenFile(string directory)
	{
		var path = Path.Combine(directory, "token");
		WriteToken(path);
		return path;
	}

	/// <summary>A copy of the token file at <paramref name="path"/> in <paramref name="copy"/>: the same secret mounted for another side.</summary>
	public static string CopyToken(string path, string copy)
	{
		File.Copy(path, copy, overwrite: true);
		return copy;
	}

	/// <summary>Writes a new random token to <paramref name="path"/>, as a rotated secret would be.</summary>
	public static void WriteToken(string path)
	{
		File.WriteAllText(path, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "\n");
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
	}

	/// <summary>A short socket path in a new directory: the 104-byte sun_path limit on macOS.</summary>
	public static string NewSocketPath()
	{
		var directory = Directory.CreateTempSubdirectory("wmn-").FullName;
		return Path.Combine(directory, "s");
	}

	/// <summary>Starts <paramref name="binary"/> standalone and waits for its ready line.</summary>
	/// <param name="arguments">More flags: <c>--fake-whatsapp</c> for a test build, <c>--media-root</c>, <c>--history-*</c>.</param>
	public static async Task<StandaloneSidecar> StartAsync(string binary, string database, string listen, string tokenPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
	{
		var info = new ProcessStartInfo(binary)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var argument in (string[])["--database", database, "--listen", listen, "--token-file", tokenPath, .. arguments])
			info.ArgumentList.Add(argument);

		var process = Process.Start(info)!;
		// EOF at once, as a container's stdin without -i: a standalone sidecar never reads it.
		process.StandardInput.Close();

		string? line;
		try
		{
			line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			process.Kill(entireProcessTree: true);
			process.Dispose();
			throw;
		}

		if (line is null)
		{
			var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			process.Dispose();
			Assert.Fail($"The standalone sidecar exited before its ready line:{Environment.NewLine}{stderr}");
		}

		using var ready = JsonDocument.Parse(line);
		return new StandaloneSidecar(process, ready.RootElement.GetProperty("listen").GetString()!);
	}

	/// <summary>
	/// Kills the process hard and returns once it is gone, without awaiting: for a caller that must not go on before,
	/// such as a log sink that runs inside the client's call.
	/// </summary>
	public void KillAndWait()
	{
		_process.Kill(entireProcessTree: true);
		_process.WaitForExit();
	}

	/// <summary>Kills the process hard, as a crashed container, and waits until it is gone.</summary>
	public async Task KillAsync()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already gone.
		}

		await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
		await _forwarding.ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		await KillAsync().ConfigureAwait(false);
		_process.Dispose();
	}

	private async Task ForwardAsync()
	{
		while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
		{
			lock (_stderr)
				_ = _stderr.AppendLine(line);
		}
	}
}
