using Whatsmeow.Core.Errors;
using Whatsmeow.Protocol.V1;
using ProtoState = Whatsmeow.Protocol.V1.SessionState;

namespace Whatsmeow.Sessions;

/// <summary>The session.proto messages as the public records.</summary>
internal static class SessionMapping
{
	public static SessionInfo ToInfo(Session session) =>
		new()
		{
			Id = SidecarValues.SessionId(session.Id, "Session.id"),
			State = ToState(session.State),
			Jid = ToJid(session.Jid),
			Lid = ToJid(session.Lid),
		};

	/// <summary>An informational JID: empty or malformed is null, so a bad value never fails a call that lists sessions.</summary>
	private static Jid? ToJid(string value) => Jid.TryParse(value, out var jid) ? jid : null;

	public static SessionState ToState(ProtoState? state) =>
		state?.KindCase switch
		{
			ProtoState.KindOneofCase.Created => new SessionState.Created(),
			ProtoState.KindOneofCase.Pairing => new SessionState.Pairing(),
			ProtoState.KindOneofCase.Connecting => new SessionState.Connecting(),
			ProtoState.KindOneofCase.Connected => new SessionState.Connected(),
			ProtoState.KindOneofCase.CaughtUp => new SessionState.CaughtUp(),
			ProtoState.KindOneofCase.Reconnecting => new SessionState.Reconnecting(),
			ProtoState.KindOneofCase.Disconnected => new SessionState.Disconnected(),
			ProtoState.KindOneofCase.LoggedOut => new SessionState.LoggedOut { Code = state.LoggedOut.Code },
			ProtoState.KindOneofCase.Replaced => new SessionState.Replaced(),
			ProtoState.KindOneofCase.TempBanned => new SessionState.TempBanned
			{
				Code = state.TempBanned.Code,
				Until = state.TempBanned.ExpireTime?.ToDateTimeOffset(),
			},
			ProtoState.KindOneofCase.Outdated => new SessionState.Outdated(),
			ProtoState.KindOneofCase.Error => new SessionState.Error { Code = state.Error.Code, Message = state.Error.Message },
			null or ProtoState.KindOneofCase.None => new SessionState.Unknown(),
			_ => new SessionState.Unknown(),
		};
}
