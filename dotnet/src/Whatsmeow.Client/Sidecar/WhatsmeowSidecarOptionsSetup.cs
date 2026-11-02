using Microsoft.Extensions.Options;
using Whatsmeow.History;

namespace Whatsmeow.Sidecar;

/// <summary>
/// The one setup step and the validation of <see cref="WhatsmeowSidecarOptions"/>. It is the only reader of
/// <c>WHATSMEOW_NET_SIDECAR_PATH</c> (docs/guidelines/options.md).
/// </summary>
internal static class WhatsmeowSidecarOptionsSetup
{
	public const string SidecarPathVariable = "WHATSMEOW_NET_SIDECAR_PATH";

	/// <summary>The database file name when none is set (ARCHITECTURE Naming).</summary>
	public const string DefaultDatabaseFile = "whatsmeow-net.db";

	/// <summary>The longest start the client waits for; far above any real start, and a value every timer accepts.</summary>
	public static TimeSpan MaxStartupTimeout { get; } = TimeSpan.FromHours(1);

	/// <summary>
	/// Applies the computed defaults; the hosting package runs it as a <c>PostConfigure</c> step. The database path
	/// becomes a full path here, so a restart finds the same file whatever the current directory is then.
	/// </summary>
	public static void Apply(WhatsmeowSidecarOptions options)
	{
		options.SidecarPath ??= Environment.GetEnvironmentVariable(SidecarPathVariable);
		options.DatabasePath ??= DefaultDatabaseFile;
		if (!string.IsNullOrWhiteSpace(options.DatabasePath))
			options.DatabasePath = Path.GetFullPath(options.DatabasePath);
		if (!string.IsNullOrWhiteSpace(options.MediaRoot))
			options.MediaRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.MediaRoot));
	}

	/// <summary>
	/// Throws <see cref="OptionsValidationException"/> listing every rule <paramref name="options"/> breaks. The
	/// hosting package runs the same rules through <see cref="WhatsmeowSidecarOptionsValidator"/>.
	/// </summary>
	public static void Validate(WhatsmeowSidecarOptions options)
	{
		var failures = Failures(options);
		if (failures.Count > 0)
			throw new OptionsValidationException(Options.DefaultName, typeof(WhatsmeowSidecarOptions), failures);
	}

	/// <summary>Every rule <paramref name="options"/> breaks, one sentence each; none when it is valid.</summary>
	public static List<string> Failures(WhatsmeowSidecarOptions options)
	{
		var failures = new List<string>();

		// Hand-written: [Range(typeof(TimeSpan), ...)] is trim-unsafe (IL2026), so no rule here fits the generator.
		if (options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout > MaxStartupTimeout)
			failures.Add($"{nameof(WhatsmeowSidecarOptions.StartupTimeout)} {options.StartupTimeout} must be more than zero and at most {MaxStartupTimeout}.");

		if (options.SidecarPath is { } path && string.IsNullOrWhiteSpace(path))
			failures.Add($"{nameof(WhatsmeowSidecarOptions.SidecarPath)} must not be empty or white space.");

		if (string.IsNullOrWhiteSpace(options.DatabasePath))
			failures.Add($"{nameof(WhatsmeowSidecarOptions.DatabasePath)} must not be empty or white space.");

		if (!Enum.IsDefined(options.Transport))
			failures.Add($"{nameof(WhatsmeowSidecarOptions.Transport)} {options.Transport} is not a transport.");

		if (options.Transport is SidecarTransport.NamedPipe && !OperatingSystem.IsWindows())
			failures.Add($"{nameof(WhatsmeowSidecarOptions.Transport)} {SidecarTransport.NamedPipe} is Windows only.");

		if (options.MediaRoot is { } root && (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)))
			failures.Add($"{nameof(WhatsmeowSidecarOptions.MediaRoot)} {root} must be an existing directory.");

		if (options.History is null)
			failures.Add($"{nameof(WhatsmeowSidecarOptions.History)} must not be null.");

		failures.AddRange(HistoryFailures(options.History));

		return failures;
	}

	/// <summary>The history options' broken rules: a limit is null, or more than zero.</summary>
	private static IEnumerable<string> HistoryFailures(HistorySyncOptions? history)
	{
		if (history is null)
			yield break;

		(string Name, int? Limit)[] limits =
		[
			(nameof(history.FullSyncDaysLimit), history.FullSyncDaysLimit),
			(nameof(history.FullSyncSizeLimitMegabytes), history.FullSyncSizeLimitMegabytes),
			(nameof(history.StorageQuotaMegabytes), history.StorageQuotaMegabytes),
		];
		foreach (var (name, limit) in limits)
		{
			if (limit is <= 0)
				yield return $"{nameof(WhatsmeowSidecarOptions.History)}.{name} must be null or more than zero.";
		}
	}
}
