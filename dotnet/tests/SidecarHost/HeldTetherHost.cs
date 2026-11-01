using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Whatsmeow;

namespace SidecarHost;

/// <summary>
/// The parent of the kill-parent test whose sidecar's stdin stays open after the parent dies: it starts the sidecar,
/// makes every handle it has inheritable, the write end of the sidecar's stdin among them, and starts a holder (this
/// program with <c>hold</c>) that inherits them and waits forever. It prints the sidecar's process id and then the
/// holder's, and waits on stdin. .NET 10 cannot pass one chosen handle to a child (ProcessStartInfo.InheritedHandles
/// is .NET 11), so every handle goes.
/// </summary>
internal static partial class HeldTetherHost
{
	public static async Task RunAsync(string sidecarPath, string database)
	{
		var logs = new FakeLogCollector();
		using var provider = new FakeLoggerProvider(logs);
		using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));

		var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = sidecarPath, DatabasePath = database }, loggerFactory, CancellationToken.None)
			.ConfigureAwait(false);
		await using (sidecar.ConfigureAwait(false))
		{
			MakeEveryHandleInheritable();
			using var holder = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "hold") { UseShellExecute = false })!;

			Console.WriteLine(logs.GetSnapshot().First(r => r.Id.Name == "SidecarStarted").GetStructuredStateValue("ProcessId"));
			Console.WriteLine(holder.Id.ToString(CultureInfo.InvariantCulture));
			await Console.Out.FlushAsync().ConfigureAwait(false);
			_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
		}
	}

	/// <summary>The holder: it only keeps what it inherited open.</summary>
	public static Task HoldAsync() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

	private static void MakeEveryHandleInheritable()
	{
		if (OperatingSystem.IsWindows())
		{
			MakeEveryWindowsHandleInheritable();
			return;
		}

		// dup clears FD_CLOEXEC on the copy (dup(2)), and .NET closes no other descriptor in a child it starts.
		var open = Directory.GetFileSystemEntries("/dev/fd").Select(entry => int.Parse(Path.GetFileName(entry), CultureInfo.InvariantCulture)).Where(fd => fd > 2).ToList();
		foreach (var fd in open)
			_ = Dup(fd);
	}

	/// <summary>
	/// Every valid handle value, the whole handle space a process can have (2^24 handles, multiples of 4), becomes
	/// inheritable, except a job: the client's kill-on-close job must close with this process, or the test proves
	/// nothing. Finding no job means the client's job went unrecognized, so the host fails instead.
	/// </summary>
	private static void MakeEveryWindowsHandleInheritable()
	{
		// JOBOBJECT_BASIC_ACCOUNTING_INFORMATION: four LARGE_INTEGERs and four DWORDs, the same 48 bytes on every
		// architecture; the query succeeds only on a job handle.
		Span<byte> accounting = stackalloc byte[48];
		var jobs = 0;
		for (nint handle = 4; handle < 1 << 26; handle += 4)
		{
			if (!GetHandleInformation(handle, out _))
				continue;

			if (QueryInformationJobObject(handle, JobObjectBasicAccountingInformation, accounting, accounting.Length, 0))
			{
				jobs++;
				continue;
			}

			_ = SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
		}

		if (jobs == 0)
			throw new InvalidOperationException("no job handle found: the client's kill-on-close job would have been inherited");
	}

	private const int JobObjectBasicAccountingInformation = 1;
	private const uint HandleFlagInherit = 1;

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("libc", EntryPoint = "dup")]
	private static partial int Dup(int fd);

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetHandleInformation(nint handle, out uint flags);

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool QueryInformationJobObject(nint job, int infoClass, Span<byte> info, int length, nint returnLength);
}
