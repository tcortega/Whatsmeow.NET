// THROWAWAY red check for #2. Every member breaks one build rule; this branch is deleted after the run.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Whatsmeow.Client;

internal static partial class Violations
{
	internal static object Time1() => DateTime.UtcNow;
	internal static object Time2() => DateTimeOffset.UtcNow;
	internal static object Time3() => DateOnly.MinValue;
	internal static object Time4() => TimeZoneInfo.Utc;
	internal static object Time5() => Stopwatch.StartNew();
	internal static Task Timer1() => Task.Delay(TimeSpan.FromSeconds(1));
	internal static object Timer2() => new CancellationTokenSource(TimeSpan.FromSeconds(1));
	internal static object Timer3() => new PeriodicTimer(TimeSpan.FromSeconds(1));
	internal static object? Refl1() => Activator.CreateInstance<object>();
	internal static object? Refl2() => Type.GetType("System.Object", throwOnError: false);
	internal static object Refl3() => typeof(List<>).MakeGenericType(typeof(int));
	internal static object? Refl4(MethodInfo method) => method.Invoke(obj: null, parameters: null);
	internal static int Block1() => Task.FromResult(1).GetAwaiter().GetResult();

	[DllImport("libc")]
	internal static extern int Native1();

	internal static async Task Await1() => await Task.Delay(TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
	internal static async void Await2() => await Task.Delay(TimeSpan.Zero, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
	internal static int Nullable1(string? value) => value.Length;
}
