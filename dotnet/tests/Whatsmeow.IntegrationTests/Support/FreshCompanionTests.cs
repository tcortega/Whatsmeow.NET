using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' fresh companion (<see cref="FreshCompanion"/>, ADR 0060), on fake-mode sidecars of its own: a new
/// database the run creates is the behaviour under test, so each test creates one and starts a sidecar on it. A paired
/// session is the fake's linked device, as the live run's QR pairing gives one.
/// </summary>
public sealed class FreshCompanionTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const string Id = "fresh";
	private const string Peer = "5511982000001";
	private const string PeerLid = "982000001";
	private const string Business = "5511982000002";

	private static readonly (Jid Chat, Jid? Lid) s_peer = (Pn(Peer), Lid(PeerLid));
	private static readonly (Jid Chat, Jid? Lid) s_business = (Pn(Business), Lid("982000002"));

	[Fact(Timeout = Timeout)]
	public async Task Run_TheExpectedAccountsNewDevice_RunsTheStepsAndTheWindow_ThenLogsOutDeletesAndKeepsTheFile()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var ran = new List<string>();

		await h.Companion.RunAsync(
			h.Run(Device(Peer, 7)) with { FirstSight = [Step("first", ran)], Then = [Step("then", ran)], WindowAsync = _ => WindowAsync(ran) },
			ct
		);

		Assert.Equal(["first", "then", "window"], ran);
		Assert.Equal([FreshEvent.IdentityVerified, FreshEvent.LogoutVerified], h.Recorded);
		Assert.Empty(await h.Sidecar.Sessions.ListAsync(ct));
		Assert.True(File.Exists(h.Database), "the database file is the caller's and stays");
	}

	[Theory(Timeout = Timeout)]
	[InlineData("the forbidden account", Business, 7, "")]
	[InlineData("a device another store has", Peer, 3, "")]
	[InlineData("a device the preflight knows", Peer, 5, "")]
	[InlineData("no known devices", Peer, 7, "none")]
	[InlineData("a known device of another account", Peer, 7, "other account")]
	[InlineData("a known device twice", Peer, 7, "twice")]
	[InlineData("a known account, not a device", Peer, 7, "device 0")]
	public async Task Run_ASessionThatIsNotTheExpectedAccountsNewDevice_IsRefused_AndNothingIsLoggedOutOrDeleted(string what, string user, ushort device, string known)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var ran = new List<string>();
		var run = h.Run(Device(user, device)) with
		{
			OtherDevices = [Device(Peer, 3)],
			KnownDevices = known switch
			{
				"none" => [],
				"other account" => [Device(Business, 9)],
				"twice" => [Device(Peer, 5), Device(Peer, 5)],
				"device 0" => [Pn(Peer)],
				_ => [Device(Peer, 5)],
			},
			FirstSight = [Step("first", ran)],
			WindowAsync = _ => WindowAsync(ran),
		};

		_ = await Assert.ThrowsAnyAsync<Exception>(() => h.Companion.RunAsync(run, ct));

		Assert.True(ran.Count == 0, $"{what}: a step or the window ran");
		Assert.Equal([FreshEvent.IdentityRejected], h.Recorded);
		await h.StillPairedAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_AFailingStepADeadlineAndACancelledBody_StillReleaseRunTheWindowLogOut_AndThrowEveryFailure()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		using var body = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var deadline = new CancellationTokenSource();
		var ran = new List<string>();
		var run = h.Run(Device(Peer, 7)) with
		{
			Deadline = deadline.Token,
			FirstSight = [new FreshStep { Name = "fails", Run = _ => throw new InvalidOperationException("boom") }, Step("after the failure", ran)],
			Then =
			[
				new FreshStep
				{
					Name = "ends the capture and the body",
					Run = async _ =>
					{
						await deadline.CancelAsync().ConfigureAwait(false);
						await body.CancelAsync().ConfigureAwait(false);
					},
				},
				Step("after the deadline", ran),
			],
			WindowAsync = token => Task.FromResult(token.IsCancellationRequested ? "cancelled" : "quiet_window"),
		};

		var thrown = await Assert.ThrowsAsync<AggregateException>(() => h.Companion.RunAsync(run, body.Token));

		Assert.Equal([typeof(InvalidOperationException), typeof(OperationCanceledException)], thrown.InnerExceptions.Select(e => e.GetType()));
		Assert.Equal(["after the failure"], ran);
		Assert.Contains(h.Log, l => string.Equals(l, "live: history window ended cancelled", StringComparison.Ordinal));
		Assert.Equal([FreshEvent.IdentityVerified, FreshEvent.LogoutVerified], h.Recorded);
		Assert.Empty(await h.Sidecar.Sessions.ListAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_HistoryDuringTheFirstSightSteps_IsHeldUntilTheyEnd_EvenWhenOneFails()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var ran = new ConcurrentQueue<string>();
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		h.Companion.HoldHistory(
			() =>
			{
				ran.Enqueue("hold start");
				_ = held.TrySetResult();
			},
			() => ran.Enqueue("hold end")
		);
		var run = h.Run(Device(Peer, 7)) with
		{
			FirstSight =
			[
				new FreshStep
				{
					Name = "history arrives, then the step fails",
					Run = async token =>
					{
						_ = await h.Fake.DeliverHistorySyncAsync(History.Histories.Blob(Id, "H1", History.Histories.Recent, History.Histories.Texts("5511982000009@s.whatsapp.net", "D", 2)), cancellationToken: token)
							.ConfigureAwait(false);
						_ = await h.Fake.CompleteOfflineSyncAsync(new CompleteOfflineSyncRequest { SessionId = Id }, cancellationToken: token).ConfigureAwait(false);
						await held.Task.WaitAsync(token).ConfigureAwait(false);
						throw new InvalidOperationException("after the hold started");
					},
				},
			],
			Then = [new FreshStep { Name = "after the release", Run = _ => EnqueueAsync(ran, "then") }],
		};

		_ = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Equal(["hold start", "hold end", "then"], ran, StringComparer.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified, FreshEvent.LogoutVerified], h.Recorded);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_AStateReadThatFailsBeforeAStep_StillRunsTheStepTheRestAndTheWindow_AndKeepsWhatItCannotLogOut()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var ran = new List<string>();
		var run = h.Run(Device(Peer, 7)) with
		{
			FirstSight =
			[
				new FreshStep
				{
					Name = "deletes the session under the client",
					Run = async token => await h.Sidecar.Sessions.DeleteAsync(SessionId.From(Id), token).ConfigureAwait(false),
				},
				Step("second", ran),
			],
			Then = [Step("then", ran)],
			WindowAsync = _ => WindowAsync(ran),
		};

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Equal(["second", "then", "window"], ran);
		Assert.Contains(h.Log, l => l.Contains("second starts with the session state unknown", StringComparison.Ordinal));
		Assert.Contains("kept", thrown.Message, StringComparison.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified], h.Recorded);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_WhenTheSessionIsLoggedOutWhileTheCleanupWaits_SaysSo_AndKeepsTheSession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var raised = Task.CompletedTask;
		// WhatsApp logs the device out while the cleanup waits for the held login.
		h.OnLog = line =>
		{
			if (line.Contains("cleanup waits", StringComparison.Ordinal))
			{
				raised = h.Fake.RaisePermanentDisconnectAsync(
					new RaisePermanentDisconnectRequest { SessionId = Id, LoggedOut = new RaisePermanentDisconnectRequest.Types.LoggedOut { Reason = 401 } },
					cancellationToken: ct
				).ResponseAsync;
			}
		};
		var run = h.Run(Device(Peer, 7)) with
		{
			Then =
			[
				new FreshStep
				{
					Name = "holds the next login and disconnects",
					Run = async token =>
					{
						_ = await h.Fake.HoldNextLoginAsync(new HoldNextLoginRequest { SessionId = Id }, cancellationToken: token).ConfigureAwait(false);
						await h.Companion.Client.DisconnectAsync(token).ConfigureAwait(false);
					},
				},
			],
		};

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));
		await raised;

		Assert.Contains("while the cleanup waited", thrown.Message, StringComparison.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified, FreshEvent.LoggedOutExternally], h.Recorded);
		Assert.Equal([SessionId.From(Id)], (await h.Sidecar.Sessions.ListAsync(ct)).Select(s => s.Id));
	}

	[Fact(Timeout = Timeout)]
	public async Task Open_ADatabaseWithASessionAlready_IsRefused_AndCreatesNoSession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);

		_ = await Assert.ThrowsAnyAsync<Exception>(() => FreshCompanion.OpenAsync(h.Sidecar, SessionId.From("second"), h.Time, _ => { }, ct));

		Assert.Equal([SessionId.From(Id)], (await h.Sidecar.Sessions.ListAsync(ct)).Select(s => s.Id));
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_OfASessionNotProven_IsRefused_AndNothingIsLoggedOut()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		await h.Run(Device(Peer, 7)).PairAsync(h.Companion, ct);

		_ = await Assert.ThrowsAsync<InvalidOperationException>(h.Companion.LogoutAsync);

		await h.StillPairedAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Run_AFailureAfterPairedBeforeTheProof_SaysTheDeviceMayBeLinked_AndLogsNothingOut()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var pair = h.Run(Device(Peer, 7)).PairAsync;
		var run = h.Run(Device(Peer, 7)) with
		{
			PairAsync = async (companion, token) =>
			{
				await pair(companion, token).ConfigureAwait(false);
				throw new InvalidOperationException("after Paired");
			},
		};

		_ = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Contains(h.Log, l => l.Contains("paired but not proven", StringComparison.Ordinal));
		Assert.Empty(h.Recorded);
		await h.StillPairedAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_WhileTheLoginIsHeldPastTheCleanupBound_KeepsTheSessionAndItsKeys()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		// The cleanup's bound passes as soon as it starts to connect or to wait: the connection never becomes usable.
		h.OnLog = line =>
		{
			if (line.Contains("cleanup connects", StringComparison.Ordinal) || line.Contains("cleanup waits", StringComparison.Ordinal))
				h.Time.Advance(FreshCompanion.CleanupBound);
		};
		var run = h.Run(Device(Peer, 7)) with
		{
			Then =
			[
				new FreshStep
				{
					Name = "holds the next login and disconnects",
					Run = async token =>
					{
						_ = await h.Fake.HoldNextLoginAsync(new HoldNextLoginRequest { SessionId = Id }, cancellationToken: token).ConfigureAwait(false);
						await h.Companion.Client.DisconnectAsync(token).ConfigureAwait(false);
					},
				},
			],
		};

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Contains("kept", thrown.Message, StringComparison.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified], h.Recorded);
		await h.StillPairedAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_WhenWhatsAppGivesNoAnswer_KeepsTheSessionAndItsKeys()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var run = h.Run(Device(Peer, 7)) with
		{
			Then =
			[
				new FreshStep
				{
					Name = "makes the next logout go unanswered",
					Run = async token => _ = await h.Fake.FailNextLogoutAsync(new FailNextLogoutRequest { SessionId = Id }, cancellationToken: token).ConfigureAwait(false),
				},
			],
		};

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Contains(nameof(OutcomeUnknownException), thrown.Message, StringComparison.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified], h.Recorded);
		await h.StillPairedAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_AfterWhatsAppLoggedTheDeviceOut_SaysSo_NeverClaimsItsOwn_AndKeepsTheSession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await Harness.StartAsync(fixture, ct);
		var run = h.Run(Device(Peer, 7)) with
		{
			Then =
			[
				new FreshStep
				{
					Name = "WhatsApp logs the device out",
					Run = async token =>
					{
						var watch = StateWatch.Start(h.Companion.Client, token);
						await using (watch.ConfigureAwait(false))
						{
							_ = await h.Fake.RaisePermanentDisconnectAsync(
								new RaisePermanentDisconnectRequest { SessionId = Id, LoggedOut = new RaisePermanentDisconnectRequest.Types.LoggedOut { Reason = 401 } },
								cancellationToken: token
							).ConfigureAwait(false);
							await watch.ReachesAsync(new SessionState.LoggedOut { Code = 401 }, new SessionState.Connected(), new SessionState.CaughtUp()).ConfigureAwait(false);
						}
					},
				},
			],
		};

		var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Companion.RunAsync(run, ct));

		Assert.Contains("logged out before its cleanup", thrown.Message, StringComparison.Ordinal);
		Assert.Equal([FreshEvent.IdentityVerified, FreshEvent.LoggedOutExternally], h.Recorded);
		Assert.Equal([SessionId.From(Id)], (await h.Sidecar.Sessions.ListAsync(ct)).Select(s => s.Id));
	}

	/// <summary>The names a fresh database may not take; Windows has no Unix mode and no unprivileged links, so only the first two there.</summary>
	public static TheoryData<string> NotNew =>
		[.. s_notNew.Take(OperatingSystem.IsWindows() ? 2 : s_notNew.Length)];

	private static readonly string[] s_notNew =
	[
		"an existing file",
		"a store's file beside it",
		"a link to an existing file",
		"a dangling link",
		"a directory others can read",
		"a linked directory",
	];

	[Theory]
	[MemberData(nameof(NotNew))]
	public void CreateExclusive_ANameThatIsNotNewInAPrivateDirectory_IsRefused_AndNothingIsWritten(string what)
	{
		var root = Directory.CreateTempSubdirectory("wmn-fresh-").FullName;
		try
		{
			var directory = Path.Combine(root, "store");
			_ = OperatingSystem.IsWindows()
				? Directory.CreateDirectory(directory)
				: Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			var path = Path.Combine(directory, "fresh.db");
			var other = Path.Combine(root, "other.db");
			File.WriteAllText(other, "a store");
			switch (what)
			{
				case "an existing file":
					File.WriteAllText(path, "a store");
					break;
				case "a link to an existing file":
					_ = File.CreateSymbolicLink(path, other);
					break;
				case "a dangling link":
					_ = File.CreateSymbolicLink(path, Path.Combine(root, "gone.db"));
					break;
				case "a store's file beside it":
					File.WriteAllText(path + "-wal", "");
					break;
				case "a directory others can read" when !OperatingSystem.IsWindows():
					File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
					break;
				default:
					var linked = Path.Combine(root, "linked");
					_ = Directory.CreateSymbolicLink(linked, directory);
					path = Path.Combine(linked, "fresh.db");
					break;
			}

			var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
			_ = Assert.ThrowsAny<IOException>(() => CreateOrWrap(path));

			Assert.Equal(before, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal), StringComparer.Ordinal);
			Assert.Equal("a store", File.ReadAllText(other));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void CreateExclusive_ANewNameInAPrivateDirectory_IsCreatedEmptyAndOwnerOnly()
	{
		var root = Directory.CreateTempSubdirectory("wmn-fresh-").FullName;
		try
		{
			var path = Path.Combine(root, "fresh.db");

			FreshCompanion.CreateExclusive(path);

			Assert.Equal(0, new FileInfo(path).Length);
			if (!OperatingSystem.IsWindows())
				Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	/// <summary>A refusal of <see cref="FreshCompanion.CreateExclusive"/>, its own or the exclusive create's, as an IOException.</summary>
	private static void CreateOrWrap(string path)
	{
		try
		{
			FreshCompanion.CreateExclusive(path);
		}
		catch (InvalidOperationException e)
		{
			throw new IOException(e.Message, e);
		}
	}

	private static Jid Pn(string user) => new() { User = user, Server = "s.whatsapp.net" };

	private static Jid Lid(string user) => new() { User = user, Server = "lid" };

	private static Jid Device(string user, ushort device) => Pn(user) with { Device = device };

	private static FreshStep Step(string name, List<string> ran) => new() { Name = name, Run = _ => RecordAsync(ran, name) };

	private static Task RecordAsync(List<string> ran, string name)
	{
		ran.Add(name);
		return Task.CompletedTask;
	}

	private static Task EnqueueAsync(ConcurrentQueue<string> ran, string name)
	{
		ran.Enqueue(name);
		return Task.CompletedTask;
	}

	private static Task<string> WindowAsync(List<string> ran)
	{
		ran.Add("window");
		return Task.FromResult("quiet_window");
	}

	/// <summary>A fresh database in a private directory of its own, a fake-mode sidecar on it, and its companion.</summary>
	private sealed class Harness : IAsyncDisposable
	{
		private readonly string _root;
		private readonly LogCapture _logs;
		private readonly ConcurrentQueue<string> _log = new();
		private readonly ConcurrentQueue<FreshEvent> _recorded = new();

		private Harness(string root, string database, LogCapture logs, WhatsmeowSidecar sidecar)
		{
			_root = root;
			Database = database;
			_logs = logs;
			Sidecar = sidecar;
			Fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);
		}

		public string Database { get; }

		public WhatsmeowSidecar Sidecar { get; }

		public FakeWhatsAppService.FakeWhatsAppServiceClient Fake { get; }

		public FakeTimeProvider Time { get; } = new();

		public FreshCompanion Companion { get; private set; } = null!;

		public Action<string> OnLog { get; set; } = _ => { };

		public IReadOnlyList<string> Log => [.. _log];

		public IReadOnlyList<FreshEvent> Recorded => [.. _recorded];

		public static async Task<Harness> StartAsync(FakeSidecar fixture, CancellationToken cancellationToken)
		{
			var root = Directory.CreateTempSubdirectory("wmn-fresh-").FullName;
			var database = Path.Combine(root, "fresh.db");
			FreshCompanion.CreateExclusive(database);
			var logs = new LogCapture();
			var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = database };
			var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, cancellationToken).ConfigureAwait(false);
			var harness = new Harness(root, database, logs, sidecar);
			harness.Companion = await FreshCompanion.OpenAsync(sidecar, SessionId.From(Id), harness.Time, harness.Write, cancellationToken).ConfigureAwait(false);
			return harness;
		}

		/// <summary>A run that links <paramref name="device"/> as the pairing would, with no steps, the peer's own account expected.</summary>
		public FreshRun Run(Jid device) =>
			new()
			{
				PairAsync = async (companion, token) =>
				{
					_ = await Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = Id, Jid = device.ToString(), Lid = $"{PeerLid}:{device.Device}@lid" }, cancellationToken: token)
						.ConfigureAwait(false);
					companion.Paired();
					await companion.Client.ConnectAsync(token).ConfigureAwait(false);
					var watch = StateWatch.Start(companion.Client, token);
					await using (watch.ConfigureAwait(false))
						await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
				},
				Expected = s_peer,
				Forbidden = s_business,
				OtherDevices = [],
				KnownDevices = [Device(Peer, 5)],
				FirstSight = [],
				Then = [],
				Deadline = CancellationToken.None,
				WindowAsync = _ => Task.FromResult("quiet_window"),
				Record = _recorded.Enqueue,
			};

		/// <summary>The session is still on the database, paired and not logged out: nothing unlinked or deleted it.</summary>
		public async Task StillPairedAsync(CancellationToken cancellationToken)
		{
			var sessions = await Sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
			var session = Assert.Single(sessions);
			_ = Assert.NotNull(session.Jid);
			Assert.IsNotType<SessionState.LoggedOut>(session.State);
		}

		public async ValueTask DisposeAsync()
		{
			Companion?.Dispose();
			await Sidecar.DisposeAsync().ConfigureAwait(false);
			_logs.Dispose();
			Directory.Delete(_root, recursive: true);
		}

		private void Write(string line)
		{
			_log.Enqueue(line);
			OnLog(line);
		}
	}
}
