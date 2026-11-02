namespace Whatsmeow.Events;

/// <summary>Why the phone did not upload media again.</summary>
public enum MediaRetryFailure
{
	/// <summary>A reason this client does not know, or an answer the sidecar could not decrypt.</summary>
	Unknown,

	/// <summary>The phone reported a general error.</summary>
	GeneralError,

	/// <summary>The phone does not have the message.</summary>
	NotFound,

	/// <summary>The phone could not decrypt the request.</summary>
	DecryptionError,

	/// <summary>The media is no longer on the phone.</summary>
	NotOnPhone,
}
