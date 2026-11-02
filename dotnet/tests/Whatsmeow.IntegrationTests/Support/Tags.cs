using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Span tags and measurement tags as dictionaries, to compare whole.</summary>
internal static class Tags
{
	public static Dictionary<string, object?> Of(params (string Key, object? Value)[] tags) => tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);

	public static Dictionary<string, object?> Of(IEnumerable<KeyValuePair<string, object?>> tags) => tags.ToDictionary(StringComparer.Ordinal);

	public static Dictionary<string, object?> Of(Activity span) => span.TagObjects.ToDictionary(StringComparer.Ordinal);

	public static Dictionary<string, object?> Of<T>(CollectedMeasurement<T> measurement)
		where T : struct => measurement.Tags.ToDictionary(StringComparer.Ordinal);
}
