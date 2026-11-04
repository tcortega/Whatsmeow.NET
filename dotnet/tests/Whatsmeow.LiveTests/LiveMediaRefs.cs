using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The refs the ref lifetime test keeps across runs (<see cref="LiveSettings.MediaRefsVariable"/>): one JSON line per
/// upload, with when it was made. The file holds media keys, so it is created owner-only, and lives outside the
/// repository.
/// </summary>
internal static partial class LiveMediaRefs
{
	/// <summary>
	/// Every ref in the file, none when there is no file or no path; a line that does not read as one is skipped and
	/// counted in <paramref name="skipped"/>. A path inside the repository is refused, so the keys are never committed.
	/// </summary>
	public static IReadOnlyList<Kept> Read(string path, out int skipped)
	{
		skipped = 0;
		if (path.Length == 0)
			return [];
		Refuse(path);
		if (!File.Exists(path))
			return [];

		var kept = new List<Kept>();
		foreach (var line in File.ReadLines(path).Where(line => line.Length > 0))
		{
			try
			{
				if (JsonSerializer.Deserialize(line, Json.Default.Line) is { } read)
				{
					kept.Add(read.ToKept());
					continue;
				}
			}
			catch (Exception e) when (e is JsonException or FormatException)
			{
				// Counted below, as a line that reads as nothing is.
			}

			skipped++;
		}

		return kept;
	}

	/// <summary>
	/// Adds <paramref name="kept"/> to the file; nothing without a path. Its directory is created owner-only, the file
	/// 0600, and an existing file is tightened to 0600 first.
	/// </summary>
	public static void Append(string path, Kept kept)
	{
		if (path.Length == 0)
			return;
		Refuse(path);
		var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
		var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
		if (OperatingSystem.IsWindows())
			_ = Directory.CreateDirectory(directory);
		if (!OperatingSystem.IsWindows())
		{
			_ = Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			if (File.Exists(path))
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		using var writer = new StreamWriter(path, options);
		writer.WriteLine(JsonSerializer.Serialize(Line.Of(kept), Json.Default.Line));
	}

	/// <summary>Refuses a path inside the repository: the file holds media keys.</summary>
	private static void Refuse(string path)
	{
		var root = Path.GetFullPath(IntegrationTests.SidecarBinaries.FindRoot());
		var full = Path.GetFullPath(path);
		var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		if (full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
			throw new InvalidOperationException($"{LiveSettings.MediaRefsVariable} names a file inside the repository; it holds media keys, so it must be outside it.");
	}

	/// <summary>A ref and when it was uploaded.</summary>
	public sealed record Kept
	{
		public required DateTimeOffset UploadedAt { get; init; }

		public required MediaRef Media { get; init; }
	}

	/// <summary>A <see cref="Kept"/> as a line of the file, the bytes in base64.</summary>
	internal sealed record Line
	{
		public required DateTimeOffset UploadedAt { get; init; }

		public required MediaType Type { get; init; }

		public required string DirectPath { get; init; }

		public required string? Url { get; init; }

		public required string MediaKey { get; init; }

		public required string FileSha256 { get; init; }

		public required string FileEncSha256 { get; init; }

		public required long FileLength { get; init; }

		public static Line Of(Kept kept) =>
			new()
			{
				UploadedAt = kept.UploadedAt,
				Type = kept.Media.Type,
				DirectPath = kept.Media.DirectPath,
				Url = kept.Media.Url?.OriginalString,
				MediaKey = Convert.ToBase64String(kept.Media.MediaKey.AsSpan()),
				FileSha256 = Convert.ToBase64String(kept.Media.FileSha256.AsSpan()),
				FileEncSha256 = Convert.ToBase64String(kept.Media.FileEncSha256.AsSpan()),
				FileLength = kept.Media.FileLength,
			};

		public Kept ToKept() =>
			new()
			{
				UploadedAt = UploadedAt,
				Media = new MediaRef
				{
					Type = Type,
					DirectPath = DirectPath,
					Url = Url is null ? null : new Uri(Url),
					MediaKey = [.. Convert.FromBase64String(MediaKey)],
					FileSha256 = [.. Convert.FromBase64String(FileSha256)],
					FileEncSha256 = [.. Convert.FromBase64String(FileEncSha256)],
					FileLength = FileLength,
					StreamingSidecar = ImmutableArray<byte>.Empty,
				},
			};
	}

	[JsonSerializable(typeof(Line))]
	[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
	private sealed partial class Json : JsonSerializerContext;
}
