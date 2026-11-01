using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Pairing;

/// <summary>The session.proto pairing steps as the public records.</summary>
internal static class PairingMapping
{
	/// <summary>The step as a public record; null for a step this client does not know, which it skips.</summary>
	public static PairingUpdate? ToUpdate(PairResponse response) =>
		response.StepCase switch
		{
			PairResponse.StepOneofCase.QrCode => new PairingUpdate.QrCode { Code = response.QrCode.Code },
			PairResponse.StepOneofCase.PairingCode => new PairingUpdate.PairingCode { Code = response.PairingCode.Code },
			PairResponse.StepOneofCase.Success => new PairingUpdate.Paired(),
			PairResponse.StepOneofCase.None => null,
			_ => null,
		};
}
