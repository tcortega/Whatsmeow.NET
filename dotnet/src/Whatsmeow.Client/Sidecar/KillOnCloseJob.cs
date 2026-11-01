using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Whatsmeow.Sidecar;

/// <summary>
/// The Windows backup to the stdin tether (ARCHITECTURE §8.5): a Job Object with <c>KILL_ON_JOB_CLOSE</c> that holds
/// the sidecar. Only this process has a handle to it, and that handle cannot be inherited, so the job closes and the
/// kernel kills the sidecar when this process dies, even while another process keeps the sidecar's stdin open.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class KillOnCloseJob
{
	private const int JobObjectExtendedLimitInformation = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	/// <summary>
	/// Puts the process in a new kill-on-close job and returns the job's only handle; closing it kills the process if
	/// it still runs. Throws <see cref="Win32Exception"/> when Windows refuses.
	/// </summary>
	public static SafeWaitHandle Assign(Process process)
	{
		// No security attributes: the handle cannot be inherited, so no other child keeps the job alive.
		var job = CreateJobObjectW(jobAttributes: 0, name: null);
		if (job.IsInvalid)
			throw new Win32Exception();

		var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
		if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, in limits, (uint)Unsafe.SizeOf<ExtendedLimitInformation>())
			&& AssignProcessToJobObject(job, process.SafeHandle))
			return job;

		var error = new Win32Exception();
		job.Dispose();
		throw error;
	}

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeWaitHandle CreateJobObjectW(nint jobAttributes, string? name);

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetInformationJobObject(SafeWaitHandle job, int infoClass, in ExtendedLimitInformation info, uint length);

	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);

	/// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> (winnt.h).</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct BasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	/// <summary><c>IO_COUNTERS</c> (winnt.h).</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	/// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> (winnt.h): <c>KILL_ON_JOB_CLOSE</c> needs this one.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct ExtendedLimitInformation
	{
		public BasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}
}
