namespace Whatsmeow.Messaging;

/// <summary>
/// How fast each session's sends leave. Every session has a budget of its own, which every kind of send takes from, in
/// the order they were made: up to <see cref="Burst"/> sends leave at once, then one every <see cref="Interval"/>, and a
/// send that has to wait for its turn waits a random part of half an interval more, so the sends do not leave on a
/// fixed beat; the jitter only delays a send, so a session never sends faster than this. WhatsApp publishes no safe
/// rates, so the defaults, a burst of 5, then one a second, are a conservative guess, not a measured limit.
/// </summary>
public sealed class SendPacingOptions
{
	/// <summary>The longest <see cref="Interval"/>: (MaxBurst - 1) intervals stay far inside the sidecar's durations.</summary>
	internal static TimeSpan MaxInterval { get; } = TimeSpan.FromHours(1);

	/// <summary>The largest <see cref="Burst"/>.</summary>
	internal const int MaxBurst = 1000;

	/// <summary>How many sends leave at once before <see cref="Interval"/> paces them; 1 to 1000.</summary>
	public int Burst { get; set; } = 5;

	/// <summary>
	/// How often a session sends once its burst is spent; more than zero and at most one hour. A
	/// send waiting for its turn still fails at once when its call is cancelled, having sent nothing.
	/// </summary>
	public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// A member-wise copy, so a property added later is copied too. Every property is a value; one whose type is a mutable
	/// class must be deep-copied here.
	/// </summary>
	internal SendPacingOptions Copy() => (SendPacingOptions)MemberwiseClone();
}
