using Whatsmeow.Protocol.V1;
using ProtoState = Whatsmeow.Protocol.V1.SessionState;

namespace Whatsmeow.Sessions;

/// <summary>The session.proto messages as the public records.</summary>
internal static class SessionMapping
{
	public static SessionInfo ToInfo(Session session) =>
		new()
		{
			Id = SessionId.From(session.Id),
			State = ToState(session.State),
			Jid = session.Jid.Length > 0 ? Jid.Parse(session.Jid) : null,
			Lid = session.Lid.Length > 0 ? Jid.Parse(session.Lid) : null,
		};

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
