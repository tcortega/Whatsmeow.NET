# 0024. Tether backups

- Status: Accepted
- Amended in place: 2026-11-12, the Windows job comes from .NET 11's `ProcessStartInfo.KillOnParentExit` (ADR 0071)
- Date: 2026-11-01
- Evidence: issue #22, its plan review, the primary sources named below, and the tests named below
- Amends: [ADR 0017](0017-sidecar-process-conventions.md) (the tether backups it left for later)

## Context

The sidecar exits with its parent through the stdin tether (#4): EOF on stdin stops it.
EOF never comes while another process holds the write end of that pipe, so ARCHITECTURE §13 invariant 9 ("the sidecar cannot outlive its parent") needs a backup on each OS.
On Windows, .NET makes its own end of the pipe non-inheritable, but only after a window inside `s_createProcessLock` where it is inheritable [observed: dotnet/runtime v10.0.14 `Process.Windows.cs:456, 817-850`], so a native spawn on another thread can inherit it [inferred].

Measured while building it:

- `PR_SET_PDEATHSIG` signals when the *thread* that forked the process exits, not only when the parent process does [observed: man `PR_SET_PDEATHSIG(2const)` CAVEATS; linux v6.12 `kernel/exit.c:707-714`, where the kernel first hands the child to another live thread of the parent].
  .NET forks from the calling thread [observed: `pal_process.c:317-362`], a thread-pool thread retires after 20 s idle [observed: `PortableThreadPool.cs:55`], and the supervisor restarts the sidecar after an await [inferred: on a pool thread].
  .NET 11's own `KillOnParentExit` forks from a dedicated thread for the same reason [observed: dotnet/runtime main `pal_process.c:311-321`]; .NET 10 has no such API [observed: the 10.0.14 reference assembly].
- The setting belongs to the calling thread: `prctl` stores it in the task, a new thread starts without it, and the kernel reads it per thread of the child [observed: `kernel/sys.c:2475`, `kernel/fork.c:2453`, `kernel/exit.c:712`].
- `getppid` names the parent process, not its thread, and the kernel updates it before it sends the signal [observed: `kernel/sys.c:984-993`, `kernel/exit.c:708-713`].
  A parent gone before the `prctl` call never signals [observed: the man page].
- macOS reparents an orphan to launchd, and `getppid` returns the new parent [observed: xnu `kern_exit.c:2473`, `kern_prot.c:174`].
- `windows.SetInformationJobObject` takes the limits as a `uintptr`, so a Go job needs `unsafe` [observed: x/sys v0.49.0 `zsyscall_windows.go:3388`]; the sidecar has none (go.md).
- With the parent gone, the read ends of stdout and stderr go too, and the sidecar died of SIGPIPE on the graceful stop's first log line: exit status -13 [observed: a local darwin/arm64 run, before this change; `TestABrokenStderrStillStopsGracefully` red].
  Go dies on a broken fd 1 or 2 unless SIGPIPE is notified or ignored [observed: go1.27.3 `runtime/signal_unix.go:335`, `os/signal/doc.go:91-111`].

## Decision

- **`--parent-pid`.** The client passes `Environment.ProcessId`; without the flag the sidecar takes `os.Getppid()` at start.
  The sidecar stops once `getppid` is no longer that pid, on the same graceful path as stdin EOF: a context cancelled with the cause "the parent exited", which the token wait and the main select already stop on.
  It logs the cause at info with `parent_pid`.
  A comparison with the parent's own pid closes the window between the fork and the sidecar's first check, as .NET 11's `expectedParentPid` does, and needs no start time against pid reuse: a reused pid is never our parent's, because a dead parent's child gets a new parent.
- **Linux.** A goroutine locked to its thread for life calls `prctl(PR_SET_PDEATHSIG, SIGUSR1)`, checks the parent right away, and again on every SIGUSR1.
  Not SIGTERM: SIGTERM always stops the sidecar, and a retired .NET thread would stop it.
  A signal with the parent alive is logged at info ("a parent-death signal came; the parent lives"): a thread of the parent exited, or a subreaper above it did (the man page).
  A SIGUSR1 after the handler is stopped does nothing: the Go runtime has no default kill for it and drops a signal nobody is notified of [observed: go1.27.3 `runtime/sigtab_linux_generic.go:20`, `signal_unix.go:735-759`].
- **macOS.** The parent check right away and every second. Not kqueue `NOTE_EXIT`: it would still need the check, for more code.
- **Windows.** The client starts the sidecar with `ProcessStartInfo.KillOnParentExit` (`Sidecar/SidecarProcess.cs`), and .NET puts it in a Job Object with `KILL_ON_JOB_CLOSE`, one job for the process's children, as the process is created or while it is still suspended [observed: dotnet/runtime `v11.0.0` `SafeProcessHandle.Windows.cs`, `CreateKillOnParentExitJob`, `PROC_THREAD_ATTRIBUTE_JOB_LIST`, `AssignJobAndResumeThread`].
  The handle is created without security attributes, so it cannot be inherited [observed: the same file; CreateJobObjectW docs], and no other child keeps the job alive.
  Windows 8 and later nest jobs, so a client that already runs in a job works [observed: AssignProcessToJobObject docs]; .NET makes a new job when the client joined another one since [observed: `ReplaceKillOnParentExitJob`].
  If Windows refuses the job, the start fails with `SidecarUnavailableException`: without the job the sidecar could outlive the client.
  Until .NET 11 the client made the job itself, one per launch, with three hand-written `[LibraryImport]` calls (`Sidecar/KillOnCloseJob.cs`, removed).
  The option is set on Windows only: on Linux it is `PR_SET_PDEATHSIG` with SIGKILL [observed: `v11.0.0` `ProcessStartInfo.cs`, `pal_process.c`], which would cut short the graceful stop the sidecar's own backup gives, and macOS has none.
- **Inherited handles.** The sidecar inherits only its stdin, stdout and stderr (`ProcessStartInfo.InheritedHandles` empty), so no other handle of the app reaches it [observed: `v11.0.0` `ProcessStartInfo.cs`; Linux `close_range`, macOS `POSIX_SPAWN_CLOEXEC_DEFAULT` in `pal_process.c`].
- **The Windows backup is a hard kill, by design.** When the client dies, the kernel terminates the sidecar as the job closes; the stop is graceful only when stdin EOF reaches the sidecar first.
  That is safe because a hard kill is already a case the sidecar must survive: the store is SQLite in WAL mode, delivery is at-least-once through the outbox (ADR 0003), and the kill-either-process tests kill it mid-stream.
  The issue's edge case "every backup uses the same graceful shutdown path" holds on Linux and macOS; on Windows it was amended to this.
- **`SidecarPath` is the binary itself.** A wrapper script that starts it no longer works on Linux and macOS: the sidecar's parent is then the script, not `--parent-pid`, so it stops at once. The option's XML doc says so.
- **Interop (until .NET 11; the library now has none).** Hand-written `[LibraryImport]` for three functions and their structs, in that one internal file.
  `[DllImport]` is banned (`dotnet/BannedSymbols.txt`), and CsWin32 emits `[LibraryImport]` only as a build task with assembly-wide `DisableRuntimeMarshalling` [observed: CsWin32 getting-started, "Support for trimming, AOT"], which is more than three functions need.
  `[LibraryImport]` needs `AllowUnsafeBlocks` in `Whatsmeow.Client`: without it the build fails with `SYSLIB1062: LibraryImportAttribute requires unsafe code. Project must be updated with '<AllowUnsafeBlocks>true</AllowUnsafeBlocks>'` [observed: SDK 10.0.403]. The library has no `unsafe` code of its own; the generator's stubs are the only users.
- **SIGPIPE** is ignored, with SIGINT, so a write to a dead parent's pipe fails instead of killing the sidecar.

## Consequences

- On Linux and macOS, every way the parent goes (stdin EOF, a dead parent, SIGTERM, Shutdown) stops the sidecar gracefully, whichever comes first.
- On Windows the client's death kills the sidecar at once, a hard kill, even when stdin EOF would have stopped it gracefully (see the Decision); the normal stop is unchanged, because the job closes only when the client is gone.
- The window on Windows between `Process.Start` and `AssignProcessToJobObject`, which .NET 10 could not close, is gone: the sidecar never runs outside the job [observed: `v11.0.0` `SafeProcessHandle.Windows.cs`, as above].
- The stop reason a signal logs is the context's cause, "terminated signal received", instead of "signal".
- A Linux `strace` shows a SIGUSR1 each time the thread that started the sidecar exits; it is expected.
- Tests: `TestTheSidecarStopsWhenItsParentDiesWithStdinOpen` (Linux, macOS), `TestAThreadOfTheParentExitingDoesNotStopTheSidecar` (Linux), `TestTheSidecarStopsWhenItStartsWithAnotherParent` (Linux, macOS), `TestABrokenStderrStillStopsGracefully` (Linux, macOS), and `ParentKilledHard_StdinHeldOpenElsewhere_SidecarExitsOnItsOwn` on all three OSes, whose host makes every handle inheritable for a holder process, because the client owns the sidecar's stdin and the host has no handle to name.
- The sidecar's own backups on Linux and macOS stay.
