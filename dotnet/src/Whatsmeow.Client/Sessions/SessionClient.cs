using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Whatsmeow.ChatState;
using Whatsmeow.Contacts;
using Whatsmeow.Core.Errors;
using Whatsmeow.Events;
using Whatsmeow.Media;
using Whatsmeow.Messaging;
using Whatsmeow.Groups;
using Whatsmeow.History;
using Whatsmeow.Pairing;
using Whatsmeow.Presence;
using ChatPresenceState = Whatsmeow.Presence.ChatPresenceState;
using PresenceState = Whatsmeow.Presence.PresenceState;
using Whatsmeow.Protocol.V1;
using BlockedUser = Whatsmeow.Contacts.BlockedUser;
using Contact = Whatsmeow.Contacts.Contact;
using GroupInfo = Whatsmeow.Groups.GroupInfo;
using ParticipantResult = Whatsmeow.Groups.ParticipantResult;
using PhoneNumberCheck = Whatsmeow.Contacts.PhoneNumberCheck;
using ResolvedJid = Whatsmeow.Contacts.ResolvedJid;
using UserInfo = Whatsmeow.Contacts.UserInfo;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Sessions;

/// <summary>A session's <see cref="IWhatsAppClient"/>: calls on the sidecar's one channel.</summary>
internal sealed class SessionClient(
	WhatsmeowSessions sessions,
	WhatsmeowSidecar sidecar,
	SessionService.SessionServiceClient client,
	CallInvoker invoker,
	SessionId sessionId,
	ILogger log
) : IWhatsAppClient, IAsyncDisposable
{
	/// <summary>How long a watch waits before it opens again after a call that ended without a status.</summary>
	private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

	private readonly SessionEvents _events = new(sidecar, invoker, sessionId, sidecar.TimeProvider, log);
	private readonly ContactService.ContactServiceClient _contacts = new(invoker);
	private readonly PresenceService.PresenceServiceClient _presence = new(invoker);
	private readonly MediaTransfer _media = new(new MediaService.MediaServiceClient(invoker), sessionId, sidecar.MediaRoot, log);
	private readonly MessageService.MessageServiceClient _messages = new(invoker);
	private readonly GroupService.GroupServiceClient _groups = new(invoker);
	private readonly ChatStateService.ChatStateServiceClient _chatState = new(invoker);
	private readonly HistoryService.HistoryServiceClient _history = new(invoker);

	public SessionId SessionId => sessionId;

	public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.On(handler);
	}

	public Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.WaitForAsync(predicate, timeout, cancellationToken);
	}

	public IDisposable OnHandlerError(Action<HandlerError> handler)
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.OnHandlerError(handler);
	}

	public Task<MediaRef> UploadMediaFileAsync(string path, MediaType type, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		return _media.UploadAsync(path, type, cancellationToken);
	}

	public Task<MediaRef> UploadMediaAsync(Stream content, MediaType type, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		return _media.UploadAsync(content, type, cancellationToken);
	}

	public Task DownloadMediaFileAsync(MediaRef media, string path, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		return _media.DownloadAsync(media, path, cancellationToken);
	}

	public Task DownloadMediaAsync(MediaRef media, Stream destination, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		return _media.DownloadAsync(media, destination, cancellationToken);
	}

	public Task RequestMediaRetryAsync(MessageId id, Events.MessageSource source, MediaRef media, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		return _media.RequestRetryAsync(id, source, media, cancellationToken);
	}

	/// <summary>Stops the session's events and acks what was handled.</summary>
	public ValueTask DisposeAsync() => _events.DisposeAsync();

	/// <summary>Stops the session's events for the sidecar's stop; see <see cref="SessionEvents.StopAsync"/>.</summary>
	internal Task StopEventsAsync(CancellationToken stopWaiting) => _events.StopAsync(stopWaiting);

	public async Task<SendResult> SendTextAsync(Jid chat, string text, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var messageId = id ?? MessageId.New();
		var response = await RpcErrors
			.CallAsync(
				_messages.SendTextAsync(
					new SendTextRequest { SessionId = sessionId.Value, Chat = chat.ToString(), ClientMessageId = messageId.Value, Text = text },
					cancellationToken: cancellationToken
				)
			)
			.ConfigureAwait(false);
		return MessagingMapping.ToResult(messageId, response.Chat, response.MessageTime);
	}

	public async Task<SendResult> SendImageAsync(Jid chat, MediaRef image, Messaging.ImageMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(image);
		ArgumentNullException.ThrowIfNull(metadata);
		var request = MediaMessageMapping.ToRequest(sessionId, chat, id ?? MessageId.New(), image, nameof(image));
		request.Image = MediaMessageMapping.ToProto(metadata);
		return await SendMediaAsync(request, cancellationToken).ConfigureAwait(false);
	}

	public async Task<SendResult> SendVideoAsync(Jid chat, MediaRef video, Messaging.VideoMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(video);
		ArgumentNullException.ThrowIfNull(metadata);
		var request = MediaMessageMapping.ToRequest(sessionId, chat, id ?? MessageId.New(), video, nameof(video));
		request.Video = MediaMessageMapping.ToProto(metadata);
		return await SendMediaAsync(request, cancellationToken).ConfigureAwait(false);
	}

	public async Task<SendResult> SendAudioAsync(Jid chat, MediaRef audio, Messaging.AudioMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(metadata);
		var request = MediaMessageMapping.ToRequest(sessionId, chat, id ?? MessageId.New(), audio, nameof(audio));
		request.Audio = MediaMessageMapping.ToProto(metadata);
		return await SendMediaAsync(request, cancellationToken).ConfigureAwait(false);
	}

	public async Task<SendResult> SendDocumentAsync(
		Jid chat,
		MediaRef document,
		Messaging.DocumentMetadata metadata,
		MessageId? id = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(metadata);
		var request = MediaMessageMapping.ToRequest(sessionId, chat, id ?? MessageId.New(), document, nameof(document));
		request.Document = MediaMessageMapping.ToProto(metadata);
		return await SendMediaAsync(request, cancellationToken).ConfigureAwait(false);
	}

	public async Task<SendResult> SendStickerAsync(
		Jid chat,
		MediaRef sticker,
		Messaging.StickerMetadata metadata,
		MessageId? id = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(sticker);
		ArgumentNullException.ThrowIfNull(metadata);
		var request = MediaMessageMapping.ToRequest(sessionId, chat, id ?? MessageId.New(), sticker, nameof(sticker));
		request.Sticker = MediaMessageMapping.ToProto(metadata);
		return await SendMediaAsync(request, cancellationToken).ConfigureAwait(false);
	}

	private async Task<SendResult> SendMediaAsync(SendMediaRequest request, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var response = await RpcErrors.CallAsync(_messages.SendMediaAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
		return MessagingMapping.ToResult(MessageId.From(request.ClientMessageId), response.Chat, response.MessageTime);
	}

	public async Task<SendResult> SendRawAsync(Jid chat, E2E.Message message, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(message);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var messageId = id ?? MessageId.New();
		var response = await RpcErrors
			.CallAsync(
				_messages.SendMessageAsync(
					new SendMessageRequest { SessionId = sessionId.Value, Chat = chat.ToString(), ClientMessageId = messageId.Value, Message = message },
					cancellationToken: cancellationToken
				)
			)
			.ConfigureAwait(false);
		return MessagingMapping.ToResult(messageId, response.Chat, response.MessageTime);
	}

	public async Task MarkReadAsync(Jid chat, IReadOnlyCollection<MessageId> messageIds, Jid? sender = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messageIds);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		_ = await RpcErrors
			.CallAsync(_messages.MarkReadAsync(MessagingMapping.ToMarkRead(sessionId, chat, messageIds, sender), cancellationToken: cancellationToken))
			.ConfigureAwait(false);
	}

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		_ = await RpcErrors
			.CallAsync(client.ConnectAsync(new ConnectRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		sessions.Remember(sessionId);

		// Events flow from the first connect in this process (ADR 0022).
		_events.Start();
	}

	public async Task DisconnectAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		// Forgotten first, so a restart in between does not connect it again.
		sessions.Forget(sessionId);
		_ = await RpcErrors
			.CallAsync(client.DisconnectAsync(new DisconnectRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
	}

	public async Task LogoutAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		// Forgotten only once it happened: a logout that failed leaves a session the application still connected.
		_ = await RpcErrors.CallAsync(client.LogoutAsync(new LogoutRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
		sessions.Forget(sessionId);
	}

	public async Task SendPresenceAsync(PresenceState state, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		_ = await RpcErrors
			.CallAsync(
				_presence.SendPresenceAsync(
					new SendPresenceRequest { SessionId = sessionId.Value, State = PresenceMapping.ToProto(state) },
					cancellationToken: cancellationToken
				)
			)
			.ConfigureAwait(false);
	}

	public async Task SubscribePresenceAsync(Jid user, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		_ = await RpcErrors
			.CallAsync(
				_presence.SubscribePresenceAsync(new SubscribePresenceRequest { SessionId = sessionId.Value, User = user.ToString() }, cancellationToken: cancellationToken)
			)
			.ConfigureAwait(false);
	}

	public async Task SendChatPresenceAsync(Jid chat, ChatPresenceState state, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		_ = await RpcErrors
			.CallAsync(
				_presence.SendChatPresenceAsync(
					new SendChatPresenceRequest { SessionId = sessionId.Value, Chat = chat.ToString(), State = PresenceMapping.ToProto(state) },
					cancellationToken: cancellationToken
				)
			)
			.ConfigureAwait(false);
	}

	public IAsyncEnumerable<PairingUpdate> PairWithQrCodeAsync(CancellationToken cancellationToken = default) =>
		PairAsync(new PairRequest { SessionId = sessionId.Value }, cancellationToken);

	public IAsyncEnumerable<PairingUpdate> PairWithPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumber);
		return PairAsync(
			new PairRequest { SessionId = sessionId.Value, Phone = new PairRequest.Types.Phone { PhoneNumber = phoneNumber } },
			cancellationToken
		);
	}

	private async IAsyncEnumerable<PairingUpdate> PairAsync(PairRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		using var call = client.Pair(request, cancellationToken: cancellationToken);

		while (await PairNextAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false))
		{
			if (PairingMapping.ToUpdate(call.ResponseStream.Current) is not { } update)
				continue;

			// The first code means pairing started: a pairing that goes on to link the device ends Connected even when
			// the caller stops reading first, so it is a session the application connected, and a sidecar restart
			// connects it again. A refused call (paired, invalid, gone) never got this far.
			if (update is PairingUpdate.QrCode or PairingUpdate.PairingCode)
				sessions.Remember(sessionId);
			yield return update;
		}
	}

	/// <summary>Reads the next pairing step: false at the end of the stream, otherwise it throws.</summary>
	private async Task<bool> PairNextAsync(IAsyncStreamReader<PairResponse> stream, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException) when (sidecar.IsDisposed)
		{
			ObjectDisposedException.ThrowIf(condition: true, sidecar);
			throw;
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}

	public async IAsyncEnumerable<SessionState> WatchStateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		while (true)
		{
			ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
			var launch = sidecar.Launches;
			using var call = client.WatchSessionState(new WatchSessionStateRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken);

			Next next;
			while ((next = await NextAsync(call.ResponseStream, launch, cancellationToken).ConfigureAwait(false)) is Next.State)
				yield return SessionMapping.ToState(call.ResponseStream.Current.State);

			if (next is Next.End)
				yield break;

			// The sidecar went away: the stream goes on from the restarted one, whose first state is its current one.
			if (next is Next.Gone)
				await sidecar.WaitForLaunchAsync(launch, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>What reading the next state found.</summary>
	private enum Next
	{
		/// <summary>A state.</summary>
		State,

		/// <summary>The stream ended.</summary>
		End,

		/// <summary>The sidecar went away: open again once it is back.</summary>
		Gone,

		/// <summary>The call ended without a status: open again now.</summary>
		Again,
	}

	/// <summary>Reads the next state, or finds why there is none; a failure the watch cannot go on from throws.</summary>
	private async Task<Next> NextAsync(IAsyncStreamReader<WatchSessionStateResponse> stream, int launch, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false) ? Next.State : Next.End;
		}
		catch (RpcException) when (sidecar.IsDisposed)
		{
			ObjectDisposedException.ThrowIf(condition: true, sidecar);
			throw;
		}
		catch (RpcException e) when (RpcErrors.IsUnreachable(e))
		{
			return Next.Gone;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// A call can end with no status at all, as one to a sidecar that is gone does [observed: WatchDownTests]. The
			// same sidecar may still be up, so it does not wait for a launch: it opens again, after a pause if none
			// happened since the call opened.
			if (sidecar.Launches == launch)
				await Task.Delay(s_retryDelay, sidecar.TimeProvider, cancellationToken).ConfigureAwait(false);
			return Next.Again;
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}

	public async Task<ImmutableArray<PhoneNumberCheck>> IsOnWhatsAppAsync(IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumbers);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new IsOnWhatsAppRequest { SessionId = sessionId.Value, PhoneNumbers = { phoneNumbers } };
		return ContactMapping.ToChecks(await RpcErrors.CallAsync(_contacts.IsOnWhatsAppAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<UserInfo>> GetUserInfoAsync(IReadOnlyList<Jid> users, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(users);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetUserInfoRequest { SessionId = sessionId.Value, Jids = { users.Select(jid => jid.ToString()) } };
		return ContactMapping.ToUsers(await RpcErrors.CallAsync(_contacts.GetUserInfoAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ProfilePicture> GetProfilePictureAsync(Jid jid, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetProfilePictureRequest { SessionId = sessionId.Value, Jid = jid.ToString() };
		return ContactMapping.ToPicture(await RpcErrors.CallAsync(_contacts.GetProfilePictureAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<ResolvedJid>> ResolveJidsAsync(IReadOnlyList<Jid> jids, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jids);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ResolveJidsRequest { SessionId = sessionId.Value, Jids = { jids.Select(jid => jid.ToString()) } };
		return ContactMapping.ToResolved(await RpcErrors.CallAsync(_contacts.ResolveJidsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ListContactsRequest { SessionId = sessionId.Value };
		return ContactMapping.ToContacts(await RpcErrors.CallAsync(_contacts.ListContactsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<BlockedUser>> GetBlocklistAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetBlocklistRequest { SessionId = sessionId.Value };
		return ContactMapping.ToBlocked(await RpcErrors.CallAsync(_contacts.GetBlocklistAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public Task BlockAsync(Jid user, CancellationToken cancellationToken = default) => UpdateBlocklistAsync(user, BlocklistAction.Block, cancellationToken);

	public Task UnblockAsync(Jid user, CancellationToken cancellationToken = default) => UpdateBlocklistAsync(user, BlocklistAction.Unblock, cancellationToken);

	private async Task UpdateBlocklistAsync(Jid user, BlocklistAction action, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new UpdateBlocklistRequest { SessionId = sessionId.Value, Jid = user.ToString(), Action = action };
		_ = await RpcErrors.CallAsync(_contacts.UpdateBlocklistAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task<ImmutableArray<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ListGroupsRequest { SessionId = sessionId.Value };
		return GroupMapping.ToGroups(await RpcErrors.CallAsync(_groups.ListGroupsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<GroupInfo> GetGroupInfoAsync(Jid group, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetGroupRequest { SessionId = sessionId.Value, Group = group.ToString() };
		return GroupMapping.ToGroup((await RpcErrors.CallAsync(_groups.GetGroupAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false)).Group);
	}

	public async Task<CreatedGroup> CreateGroupAsync(string name, IReadOnlyList<Jid> participants, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(participants);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new CreateGroupRequest { SessionId = sessionId.Value, Name = name, Participants = { participants.Select(jid => jid.ToString()) } };
		return GroupMapping.ToCreated(await RpcErrors.CallAsync(_groups.CreateGroupAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public Task<ImmutableArray<ParticipantResult>> AddParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		UpdateParticipantsAsync(group, users, ParticipantAction.Add, cancellationToken);

	public Task<ImmutableArray<ParticipantResult>> RemoveParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		UpdateParticipantsAsync(group, users, ParticipantAction.Remove, cancellationToken);

	public Task<ImmutableArray<ParticipantResult>> PromoteParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		UpdateParticipantsAsync(group, users, ParticipantAction.Promote, cancellationToken);

	public Task<ImmutableArray<ParticipantResult>> DemoteParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		UpdateParticipantsAsync(group, users, ParticipantAction.Demote, cancellationToken);

	private async Task<ImmutableArray<ParticipantResult>> UpdateParticipantsAsync(Jid group, IReadOnlyList<Jid> users, ParticipantAction action, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(users);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new UpdateParticipantsRequest
		{
			SessionId = sessionId.Value,
			Group = group.ToString(),
			Participants = { users.Select(jid => jid.ToString()) },
			Action = action,
		};
		var response = await RpcErrors.CallAsync(_groups.UpdateParticipantsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
		return GroupMapping.ToResults(response.Participants);
	}

	public async Task SetGroupNameAsync(Jid group, string name, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new SetGroupNameRequest { SessionId = sessionId.Value, Group = group.ToString(), Name = name };
		_ = await RpcErrors.CallAsync(_groups.SetGroupNameAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task SetGroupTopicAsync(Jid group, string? topic, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new SetGroupTopicRequest { SessionId = sessionId.Value, Group = group.ToString(), Topic = topic ?? "" };
		_ = await RpcErrors.CallAsync(_groups.SetGroupTopicAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task SetGroupAnnounceAsync(Jid group, bool announce, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new SetGroupAnnounceRequest { SessionId = sessionId.Value, Group = group.ToString(), Announce = announce };
		_ = await RpcErrors.CallAsync(_groups.SetGroupAnnounceAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task SetGroupLockedAsync(Jid group, bool locked, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new SetGroupLockedRequest { SessionId = sessionId.Value, Group = group.ToString(), Locked = locked };
		_ = await RpcErrors.CallAsync(_groups.SetGroupLockedAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task<Uri> GetGroupInviteLinkAsync(Jid group, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetInviteLinkRequest { SessionId = sessionId.Value, Group = group.ToString() };
		var response = await RpcErrors.CallAsync(_groups.GetInviteLinkAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
		return new Uri(response.Link, UriKind.RelativeOrAbsolute);
	}

	public async Task<Uri> ResetGroupInviteLinkAsync(Jid group, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ResetInviteLinkRequest { SessionId = sessionId.Value, Group = group.ToString() };
		var response = await RpcErrors.CallAsync(_groups.ResetInviteLinkAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
		return new Uri(response.Link, UriKind.RelativeOrAbsolute);
	}

	public async Task<Jid> JoinGroupWithLinkAsync(Uri link, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(link);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new JoinGroupWithLinkRequest { SessionId = sessionId.Value, Link = link.OriginalString };
		return Jid.Parse((await RpcErrors.CallAsync(_groups.JoinGroupWithLinkAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false)).Group);
	}

	public async Task LeaveGroupAsync(Jid group, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new LeaveGroupRequest { SessionId = sessionId.Value, Group = group.ToString() };
		_ = await RpcErrors.CallAsync(_groups.LeaveGroupAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public Task MuteChatAsync(Jid chat, DateTimeOffset? until = null, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Mute = ChatStateMapping.ToMute(until) }, cancellationToken);

	public Task UnmuteChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Mute = new ChatMute() }, cancellationToken);

	public Task PinChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Pin = new ChatPin { Pinned = true } }, cancellationToken);

	public Task UnpinChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Pin = new ChatPin() }, cancellationToken);

	public Task ArchiveChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Archive = new ChatArchive { Archived = true } }, cancellationToken);

	public Task UnarchiveChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Archive = new ChatArchive() }, cancellationToken);

	public Task MarkChatReadAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Read = new ChatRead { Read = true } }, cancellationToken);

	public Task MarkChatUnreadAsync(Jid chat, CancellationToken cancellationToken = default) =>
		SetChatStateAsync(new SetChatStateRequest { Chat = chat.ToString(), Read = new ChatRead() }, cancellationToken);

	public Task StarMessageAsync(MessageId id, Events.MessageSource source, CancellationToken cancellationToken = default) => StarAsync(id, source, starred: true, cancellationToken);

	public Task UnstarMessageAsync(MessageId id, Events.MessageSource source, CancellationToken cancellationToken = default) =>
		StarAsync(id, source, starred: false, cancellationToken);

	private Task StarAsync(MessageId id, Events.MessageSource source, bool starred, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		return SetChatStateAsync(new SetChatStateRequest { Chat = source.Chat.ToString(), Star = ChatStateMapping.ToStar(id, source, starred) }, cancellationToken);
	}

	private async Task SetChatStateAsync(SetChatStateRequest request, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		request.SessionId = sessionId.Value;
		_ = await RpcErrors.CallAsync(_chatState.SetChatStateAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}

	public async Task RequestHistoryAsync(Jid chat, MessageId oldest, bool oldestIsFromMe, DateTimeOffset oldestTimestamp, int count = 50, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = HistoryMapping.ToRequest(sessionId, chat, oldest, oldestIsFromMe, oldestTimestamp, count);
		_ = await RpcErrors.CallAsync(_history.RequestHistoryAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}
}
