namespace Whatsmeow.Events;

/// <summary>The phone's answer to a media retry request; each case is a nested sealed record.</summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record MediaRetryResult
{
	private protected MediaRetryResult()
	{
	}

	/// <summary>The phone uploaded the media again: the requested media at its new path, which downloads.</summary>
	public sealed record Reuploaded : MediaRetryResult
	{
		/// <summary>The media.</summary>
		public required MediaRef Media { get; init; }
	}

	/// <summary>The phone could not upload the media again.</summary>
	public sealed record Failed : MediaRetryResult
	{
		/// <summary>Why.</summary>
		public required MediaRetryFailure Reason { get; init; }
	}
}
