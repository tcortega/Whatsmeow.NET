using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Whatsmeow.Contacts;
using Whatsmeow.Core.Dispatching;
using Whatsmeow.Events;
using Whatsmeow.Groups;
using Whatsmeow.Messaging;
using Whatsmeow.Pairing;
using Whatsmeow.Presence;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Testing;

/// <summary>
/// <para>
/// An <see cref="IWhatsAppClient"/> for an application's own tests: no sidecar, no phone, no WhatsApp (ADR 0012, ADR 0036).
/// A test raises events with <see cref="RaiseAsync"/>, reads what the code under test called in <see cref="Calls"/>, and
/// makes the next call of a member fail with <see cref="FailNext"/>.
/// </para>
/// <para>
/// Events run in the client's own dispatcher, so <see cref="On{TEvent}"/>, <see cref="WaitForAsync{TEvent}"/> and
/// <see cref="OnHandlerError"/> behave as on a real session: one chat's events reach the handlers one at a time, in
/// order, different chats run concurrently, presence and typing run in lanes of their own that never wait for the other
/// events, history runs in one lane of its own that never waits for a chat's, and a handler that throws is reported on
/// <see cref="OnHandlerError"/> while the event still counts as handled.
/// </para>
/// <para>
/// Every other member is recorded and returns a plausible result at once; the fake enforces no WhatsApp rule, paces
/// nothing, and fails only when told to. A send reports the requested chat, the given message id (a new one when none
/// is given) and the time of the fake's <see cref="TimeProvider"/>. Groups and invite links get JIDs and links of their
/// own, numbered in the order they are made; participant changes succeed; every phone number is on WhatsApp; contacts,
/// the blocklist and the groups are empty; an upload reads its content and returns a reference with its length and
/// SHA-256; a download writes nothing (to a path, an empty file); pairing yields one code, then
/// <see cref="PairingUpdate.Paired"/>; the state watch yields <see cref="SessionState.Connected"/>, then waits until it
/// is cancelled. A call whose token is already cancelled throws <see cref="OperationCanceledException"/>, and every call
/// once the fake is disposed throws <see cref="ObjectDisposedException"/>, as the client's do once its sidecar is
/// disposed; neither is recorded.
/// </para>
/// </summary>
public sealed class FakeWhatsAppClient : IWhatsAppClient, IAsyncDisposable
{
	private const long FirstGroup = 120363000000000001;

	/// <summary>The members <see cref="Calls"/> records, the ones <see cref="FailNext"/> can fail.</summary>
	private static readonly FrozenSet<string> s_calls = new[]
	{
		nameof(ConnectAsync),
		nameof(DisconnectAsync),
		nameof(PairWithQrCodeAsync),
		nameof(PairWithPhoneNumberAsync),
		nameof(LogoutAsync),
		nameof(SendPresenceAsync),
		nameof(SubscribePresenceAsync),
		nameof(SendChatPresenceAsync),
		nameof(WatchStateAsync),
		nameof(UploadMediaFileAsync),
		nameof(UploadMediaAsync),
		nameof(DownloadMediaFileAsync),
		nameof(DownloadMediaAsync),
		nameof(RequestMediaRetryAsync),
		nameof(IsOnWhatsAppAsync),
		nameof(GetUserInfoAsync),
		nameof(GetProfilePictureAsync),
		nameof(ResolveJidsAsync),
		nameof(GetContactsAsync),
		nameof(GetBlocklistAsync),
		nameof(BlockAsync),
		nameof(UnblockAsync),
		nameof(SendTextAsync),
		nameof(SendImageAsync),
		nameof(SendVideoAsync),
		nameof(SendAudioAsync),
		nameof(SendDocumentAsync),
		nameof(SendStickerAsync),
		nameof(SendRawAsync),
		nameof(SendReactionAsync),
		nameof(EditMessageAsync),
		nameof(RevokeMessageAsync),
		nameof(MarkReadAsync),
		nameof(GetGroupsAsync),
		nameof(GetGroupInfoAsync),
		nameof(CreateGroupAsync),
		nameof(AddParticipantsAsync),
		nameof(RemoveParticipantsAsync),
		nameof(PromoteParticipantsAsync),
		nameof(DemoteParticipantsAsync),
		nameof(SetGroupNameAsync),
		nameof(SetGroupTopicAsync),
		nameof(SetGroupAnnounceAsync),
		nameof(SetGroupLockedAsync),
		nameof(GetGroupInviteLinkAsync),
		nameof(ResetGroupInviteLinkAsync),
		nameof(JoinGroupWithLinkAsync),
		nameof(LeaveGroupAsync),
		nameof(MuteChatAsync),
		nameof(UnmuteChatAsync),
		nameof(PinChatAsync),
		nameof(UnpinChatAsync),
		nameof(ArchiveChatAsync),
		nameof(UnarchiveChatAsync),
		nameof(MarkChatReadAsync),
		nameof(MarkChatUnreadAsync),
		nameof(StarMessageAsync),
		nameof(UnstarMessageAsync),
		nameof(RequestHistoryAsync),
	}.ToFrozenSet(StringComparer.Ordinal);

	private readonly TimeProvider _time;
	private readonly EventPipeline _pipeline;

	// Never disposed: a raise that is writing when the fake is disposed still releases it.
#pragma warning disable CA2213 // Justification: see above; unused, it holds no unmanaged resource
	private readonly SemaphoreSlim _raising = new(1);
#pragma warning restore CA2213
	private readonly Lock _lock = new();
	private readonly List<FakeCall> _calls = [];
	private readonly Dictionary<string, Queue<Exception>> _failures = new(StringComparer.Ordinal);

	/// <summary>The lane whose handlers run on this flow, so a raise into it from one of them fails instead of waiting forever.</summary>
	private readonly AsyncLocal<RunningLane?> _runningLane = new();
	private ulong _raised;
	private long _made;

	/// <summary>Creates the fake of session <paramref name="sessionId"/>.</summary>
	/// <param name="sessionId">The session, which every raised event must belong to.</param>
	/// <param name="timeProvider">
	/// The time of every result and call, and the clock of <see cref="WaitForAsync{TEvent}"/>'s timeout; the system's when null.
	/// </param>
	public FakeWhatsAppClient(SessionId sessionId, TimeProvider? timeProvider = null)
	{
		SessionId = sessionId;
#pragma warning disable RS0030 // Justification: the default when the test gives none (docs/guidelines/csharp.md Time)
		_time = timeProvider ?? TimeProvider.System;
#pragma warning restore RS0030
		_pipeline = new EventPipeline(sessionId, ackAsync: null, _time, NullLogger.Instance);
		_pipeline.Start();
	}

	/// <inheritdoc/>
	public SessionId SessionId { get; }

	/// <summary>Every call so far, in the order they were made; <see cref="On{TEvent}"/>, <see cref="WaitForAsync{TEvent}"/> and <see cref="OnHandlerError"/> are not calls.</summary>
	public ImmutableArray<FakeCall> Calls
	{
		get
		{
			lock (_lock)
				return [.. _calls];
		}
	}

	/// <summary>
	/// <para>
	/// Raises <paramref name="e"/> as the session's next event, and returns once every handler of it finished. A handler
	/// that throws does not fail this: it is reported on <see cref="OnHandlerError"/>, as on a real session. An event no
	/// handler takes is ignored.
	/// </para>
	/// <para>
	/// The event waits for the earlier ones of its lane, its chat's for most events, so a raise into the lane of the
	/// handler it is called from would wait forever: it throws <see cref="InvalidOperationException"/> instead, awaited or
	/// not. A raise into another lane runs, but handlers that wait for each other's raises across lanes still wait forever,
	/// and so do raises from the handlers of 16 lanes at once, the most that run at a time.
	/// </para>
	/// </summary>
	/// <param name="e">The event, of this fake's session.</param>
	/// <param name="cancellationToken">Stops waiting; an event already handed to the dispatcher still runs.</param>
	/// <exception cref="ArgumentException"><paramref name="e"/> is of another session.</exception>
	/// <exception cref="InvalidOperationException">It is called from a handler of the lane <paramref name="e"/> runs in.</exception>
	/// <exception cref="ObjectDisposedException">The fake was disposed, before or while the event waited.</exception>
	[SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "It raises a WhatsApp event into the handlers and waits for them; it is no .NET event.")]
	public async Task RaiseAsync(WhatsAppEvent e, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(e);
		if (e.SessionId != SessionId)
			throw new ArgumentException($"The event is of session {e.SessionId.Value}, and this fake is session {SessionId.Value}.", nameof(e));
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);

		var ephemeral = EventMapping.IsEphemeral(e);
		var lane = EventMapping.Lane(e);
		if (_runningLane.Value is { Finished: false } running && running.Ephemeral == ephemeral && string.Equals(running.Lane, lane, StringComparison.Ordinal))
			throw new InvalidOperationException(
				$"A handler raised a {e.GetType().Name} into its own lane (\"{lane}\"): it would wait behind the handler that waits for it. Raise it from the test instead."
			);

		var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _pipeline.Stopping);
		try
		{
			await _raising.WaitAsync(waiting.Token).ConfigureAwait(false);
			try
			{
				var work = _pipeline.ToWork(++_raised, e);
				var current = new RunningLane(ephemeral, work.Lane);
				var raised = new Work
				{
					Seq = work.Seq,
					Lane = work.Lane,
					RunAsync = async token =>
					{
						_runningLane.Value = current;
						try
						{
							await work.RunAsync(token).ConfigureAwait(false);
						}
						finally
						{
							current.Finished = true;
						}

						_ = handled.TrySetResult();
					},
				};
				var writer = ephemeral ? _pipeline.Ephemeral : _pipeline.Durable;
				await writer.WriteAsync(raised, waiting.Token).ConfigureAwait(false);
			}
			finally
			{
				_ = _raising.Release();
			}

			await handled.Task.WaitAsync(waiting.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The fake was disposed: the event never runs, or its handlers were stopped.
			ObjectDisposedException.ThrowIf(condition: true, this);
			throw;
		}
	}

	/// <summary>
	/// Makes the next call of <paramref name="member"/> throw <paramref name="exception"/>; the call is still recorded.
	/// Each call takes one, in the order they were given. A stream (pairing, the state watch) throws it when it is first
	/// read. An exception of one reason carries its code as the library's does (<c>new RecipientNotFoundException("...")</c>);
	/// for the others, set what the library's carries:
	/// <c>new WhatsmeowException("...") { ErrorCode = WhatsmeowErrorCode.InviteLinkInvalid }</c>.
	/// </summary>
	/// <param name="member">The member, as <c>nameof(IWhatsAppClient.SendTextAsync)</c> names it.</param>
	/// <param name="exception">What the call throws.</param>
	/// <exception cref="ArgumentException"><paramref name="member"/> is not a member <see cref="Calls"/> records.</exception>
	public void FailNext(string member, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(member);
		ArgumentNullException.ThrowIfNull(exception);
		if (!s_calls.Contains(member))
			throw new ArgumentException($"\"{member}\" is not a member the fake records; name one with nameof(IWhatsAppClient.SendTextAsync).", nameof(member));
		lock (_lock)
		{
			if (!_failures.TryGetValue(member, out var queue))
				_failures[member] = queue = new Queue<Exception>();
			queue.Enqueue(exception);
		}
	}

	/// <summary>Stops the events: running handlers see their token cancelled and are waited for; no event runs any more.</summary>
	public ValueTask DisposeAsync() => _pipeline.DisposeAsync();

	/// <inheritdoc/>
	public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		return _pipeline.On(handler);
	}

	/// <inheritdoc/>
	public Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		return _pipeline.WaitForAsync(predicate, timeout, cancellationToken);
	}

	/// <inheritdoc/>
	public IDisposable OnHandlerError(Action<HandlerError> handler)
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		return _pipeline.OnHandlerError(handler);
	}

	/// <inheritdoc/>
	public Task ConnectAsync(CancellationToken cancellationToken = default) => CallAsync(nameof(ConnectAsync), [], cancellationToken);

	/// <inheritdoc/>
	public Task DisconnectAsync(CancellationToken cancellationToken = default) => CallAsync(nameof(DisconnectAsync), [], cancellationToken);

	/// <inheritdoc/>
	public IAsyncEnumerable<PairingUpdate> PairWithQrCodeAsync(CancellationToken cancellationToken = default) =>
		PairAsync(nameof(PairWithQrCodeAsync), [], () => new PairingUpdate.QrCode { Code = Invariant($"2@fake-qr-{Make()}") }, cancellationToken);

	/// <inheritdoc/>
	public IAsyncEnumerable<PairingUpdate> PairWithPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumber);
		return PairAsync(
			nameof(PairWithPhoneNumberAsync),
			[(nameof(phoneNumber), phoneNumber)],
			() => new PairingUpdate.PairingCode { Code = Invariant($"FAKE-{Make() % 10000:D4}") },
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task LogoutAsync(CancellationToken cancellationToken = default) => CallAsync(nameof(LogoutAsync), [], cancellationToken);

	/// <inheritdoc/>
	public Task SendPresenceAsync(PresenceState state, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SendPresenceAsync), [(nameof(state), state)], cancellationToken);

	/// <inheritdoc/>
	public Task SubscribePresenceAsync(Jid user, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SubscribePresenceAsync), [(nameof(user), user)], cancellationToken);

	/// <inheritdoc/>
	public Task SendChatPresenceAsync(Jid chat, ChatPresenceState state, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SendChatPresenceAsync), [(nameof(chat), chat), (nameof(state), state)], cancellationToken);

	/// <inheritdoc/>
	public async IAsyncEnumerable<SessionState> WatchStateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Begin(cancellationToken);
		Fail(Record(nameof(WatchStateAsync), []));
		yield return new SessionState.Connected();
		await Task.Delay(Timeout.InfiniteTimeSpan, _time, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<MediaRef> UploadMediaFileAsync(string path, MediaType type, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(path);
		Begin(cancellationToken);

		// Recorded before the file is read, as the call is made: a missing file is still a call.
		Fail(Record(nameof(UploadMediaFileAsync), [(nameof(path), path), (nameof(type), type)]));
		var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		return ToRef(type, content);
	}

	/// <inheritdoc/>
	public async Task<MediaRef> UploadMediaAsync(Stream content, MediaType type, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(content);
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		var read = new MemoryStream();
		await using (read.ConfigureAwait(false))
		{
			await content.CopyToAsync(read, cancellationToken).ConfigureAwait(false);
			ImmutableArray<byte> bytes = [.. read.ToArray()];
			return await CallAsync(nameof(UploadMediaAsync), [(nameof(content), bytes), (nameof(type), type)], () => ToRef(type, bytes.AsSpan()), cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <inheritdoc/>
	public async Task DownloadMediaFileAsync(MediaRef media, string path, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(media);
		ArgumentNullException.ThrowIfNull(path);
		await CallAsync(nameof(DownloadMediaFileAsync), [(nameof(media), media), (nameof(path), path)], cancellationToken).ConfigureAwait(false);
		await File.WriteAllBytesAsync(path, [], cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public Task DownloadMediaAsync(MediaRef media, Stream destination, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(media);
		ArgumentNullException.ThrowIfNull(destination);
		return CallAsync(nameof(DownloadMediaAsync), [(nameof(media), media), (nameof(destination), destination)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task RequestMediaRetryAsync(MessageId id, MessageSource source, MediaRef media, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(media);
		return CallAsync(nameof(RequestMediaRetryAsync), [(nameof(id), id), (nameof(source), source), (nameof(media), media)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task<ImmutableArray<PhoneNumberCheck>> IsOnWhatsAppAsync(IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumbers);
		ImmutableArray<string> numbers = [.. phoneNumbers];
		return CallAsync(
			nameof(IsOnWhatsAppAsync),
			[(nameof(phoneNumbers), numbers)],
			() =>
				numbers
					.Select(static number => new PhoneNumberCheck
					{
						PhoneNumber = number,
						IsOnWhatsApp = true,
						Jid = new Jid { User = string.Concat(number.Where(char.IsAsciiDigit)), Server = "s.whatsapp.net" },
						Lid = null,
					})
					.ToImmutableArray(),
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task<ImmutableArray<UserInfo>> GetUserInfoAsync(IReadOnlyList<Jid> users, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(users);
		ImmutableArray<Jid> copy = [.. users];
		return CallAsync(nameof(GetUserInfoAsync), [(nameof(users), copy)], () => ToUserInfo(copy), cancellationToken);
	}

	/// <inheritdoc/>
	public Task<ProfilePicture> GetProfilePictureAsync(Jid jid, CancellationToken cancellationToken = default) =>
		CallAsync<ProfilePicture>(nameof(GetProfilePictureAsync), [(nameof(jid), jid)], static () => new ProfilePicture.None(), cancellationToken);

	/// <inheritdoc/>
	public Task<ImmutableArray<ResolvedJid>> ResolveJidsAsync(IReadOnlyList<Jid> jids, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jids);
		ImmutableArray<Jid> copy = [.. jids];
		return CallAsync(
			nameof(ResolveJidsAsync),
			[(nameof(jids), copy)],
			() => copy.Select(static jid => new ResolvedJid { Jid = jid, JidAlt = null }).ToImmutableArray(),
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task<ImmutableArray<Contact>> GetContactsAsync(CancellationToken cancellationToken = default) =>
		CallAsync(nameof(GetContactsAsync), [], static () => ImmutableArray<Contact>.Empty, cancellationToken);

	/// <inheritdoc/>
	public Task<ImmutableArray<BlockedUser>> GetBlocklistAsync(CancellationToken cancellationToken = default) =>
		CallAsync(nameof(GetBlocklistAsync), [], static () => ImmutableArray<BlockedUser>.Empty, cancellationToken);

	/// <inheritdoc/>
	public Task BlockAsync(Jid user, CancellationToken cancellationToken = default) => CallAsync(nameof(BlockAsync), [(nameof(user), user)], cancellationToken);

	/// <inheritdoc/>
	public Task UnblockAsync(Jid user, CancellationToken cancellationToken = default) => CallAsync(nameof(UnblockAsync), [(nameof(user), user)], cancellationToken);

#pragma warning disable RS0026 // Justification: as on IWhatsAppClient, each options overload requires its options, so no call binds to both overloads (ADR 0038)
	/// <inheritdoc/>
	public Task<SendResult> SendTextAsync(Jid chat, string text, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return SendAsync(nameof(SendTextAsync), [(nameof(chat), chat), (nameof(text), text), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendImageAsync(Jid chat, MediaRef image, ImageMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(image);
		ArgumentNullException.ThrowIfNull(metadata);
		return SendAsync(nameof(SendImageAsync), [(nameof(chat), chat), (nameof(image), image), (nameof(metadata), metadata), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendVideoAsync(Jid chat, MediaRef video, VideoMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(video);
		ArgumentNullException.ThrowIfNull(metadata);
		return SendAsync(nameof(SendVideoAsync), [(nameof(chat), chat), (nameof(video), video), (nameof(metadata), metadata), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendAudioAsync(Jid chat, MediaRef audio, AudioMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(metadata);
		return SendAsync(nameof(SendAudioAsync), [(nameof(chat), chat), (nameof(audio), audio), (nameof(metadata), metadata), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendDocumentAsync(Jid chat, MediaRef document, DocumentMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(metadata);
		return SendAsync(
			nameof(SendDocumentAsync),
			[(nameof(chat), chat), (nameof(document), document), (nameof(metadata), metadata), (nameof(id), id)],
			chat,
			id,
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendStickerAsync(Jid chat, MediaRef sticker, StickerMetadata metadata, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sticker);
		ArgumentNullException.ThrowIfNull(metadata);
		return SendAsync(
			nameof(SendStickerAsync),
			[(nameof(chat), chat), (nameof(sticker), sticker), (nameof(metadata), metadata), (nameof(id), id)],
			chat,
			id,
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendTextAsync(Jid chat, string text, SendOptions options, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentNullException.ThrowIfNull(options);
		return SendAsync(nameof(SendTextAsync), [(nameof(chat), chat), (nameof(text), text), (nameof(options), options), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> SendImageAsync(Jid chat, MediaRef image, ImageMetadata metadata, SendOptions options, MessageId? id = null, CancellationToken cancellationToken = default) =>
		SendMediaAsync(nameof(SendImageAsync), chat, (nameof(image), image), metadata, options, id, cancellationToken);

	/// <inheritdoc/>
	public Task<SendResult> SendVideoAsync(Jid chat, MediaRef video, VideoMetadata metadata, SendOptions options, MessageId? id = null, CancellationToken cancellationToken = default) =>
		SendMediaAsync(nameof(SendVideoAsync), chat, (nameof(video), video), metadata, options, id, cancellationToken);

	/// <inheritdoc/>
	public Task<SendResult> SendAudioAsync(Jid chat, MediaRef audio, AudioMetadata metadata, SendOptions options, MessageId? id = null, CancellationToken cancellationToken = default) =>
		SendMediaAsync(nameof(SendAudioAsync), chat, (nameof(audio), audio), metadata, options, id, cancellationToken);

	/// <inheritdoc/>
	public Task<SendResult> SendDocumentAsync(
		Jid chat,
		MediaRef document,
		DocumentMetadata metadata,
		SendOptions options,
		MessageId? id = null,
		CancellationToken cancellationToken = default
	) => SendMediaAsync(nameof(SendDocumentAsync), chat, (nameof(document), document), metadata, options, id, cancellationToken);

	/// <inheritdoc/>
	public Task<SendResult> SendStickerAsync(
		Jid chat,
		MediaRef sticker,
		StickerMetadata metadata,
		SendOptions options,
		MessageId? id = null,
		CancellationToken cancellationToken = default
	) => SendMediaAsync(nameof(SendStickerAsync), chat, (nameof(sticker), sticker), metadata, options, id, cancellationToken);
#pragma warning restore RS0026

	/// <inheritdoc/>
	public Task<SendResult> SendReactionAsync(Jid chat, MessageKey target, string emoji, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(emoji);
		return SendAsync(nameof(SendReactionAsync), [(nameof(chat), chat), (nameof(target), target), (nameof(emoji), emoji), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> EditMessageAsync(Jid chat, MessageId target, string text, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return SendAsync(nameof(EditMessageAsync), [(nameof(chat), chat), (nameof(target), target), (nameof(text), text), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task<SendResult> RevokeMessageAsync(Jid chat, MessageKey target, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(target);
		return SendAsync(nameof(RevokeMessageAsync), [(nameof(chat), chat), (nameof(target), target), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	[Experimental("WMN1001")]
	public Task<SendResult> SendRawAsync(Jid chat, E2E.Message message, MessageId? id = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(message);
		return SendAsync(nameof(SendRawAsync), [(nameof(chat), chat), (nameof(message), message.Clone()), (nameof(id), id)], chat, id, cancellationToken);
	}

	/// <inheritdoc/>
	public Task MarkReadAsync(Jid chat, IReadOnlyCollection<MessageId> messageIds, Jid? sender = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messageIds);
		ImmutableArray<MessageId> ids = [.. messageIds];
		return CallAsync(nameof(MarkReadAsync), [(nameof(chat), chat), (nameof(messageIds), ids), (nameof(sender), sender)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task<ImmutableArray<GroupInfo>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
		CallAsync(nameof(GetGroupsAsync), [], static () => ImmutableArray<GroupInfo>.Empty, cancellationToken);

	/// <inheritdoc/>
	public Task<GroupInfo> GetGroupInfoAsync(Jid group, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(GetGroupInfoAsync), [(nameof(group), group)], () => ToGroup(group, name: "", createdAt: null, []), cancellationToken);

	/// <inheritdoc/>
	public Task<CreatedGroup> CreateGroupAsync(string name, IReadOnlyList<Jid> participants, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(participants);
		ImmutableArray<Jid> copy = [.. participants];
		return CallAsync(
			nameof(CreateGroupAsync),
			[(nameof(name), name), (nameof(participants), copy)],
			() => new CreatedGroup { Group = ToGroup(NewGroup(), name, _time.GetUtcNow(), copy), Participants = Done(copy) },
			cancellationToken
		);
	}

	/// <inheritdoc/>
	public Task<ImmutableArray<ParticipantResult>> AddParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		ParticipantsAsync(nameof(AddParticipantsAsync), group, users, cancellationToken);

	/// <inheritdoc/>
	public Task<ImmutableArray<ParticipantResult>> RemoveParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		ParticipantsAsync(nameof(RemoveParticipantsAsync), group, users, cancellationToken);

	/// <inheritdoc/>
	public Task<ImmutableArray<ParticipantResult>> PromoteParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		ParticipantsAsync(nameof(PromoteParticipantsAsync), group, users, cancellationToken);

	/// <inheritdoc/>
	public Task<ImmutableArray<ParticipantResult>> DemoteParticipantsAsync(Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken = default) =>
		ParticipantsAsync(nameof(DemoteParticipantsAsync), group, users, cancellationToken);

	/// <inheritdoc/>
	public Task SetGroupNameAsync(Jid group, string name, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		return CallAsync(nameof(SetGroupNameAsync), [(nameof(group), group), (nameof(name), name)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task SetGroupTopicAsync(Jid group, string? topic, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SetGroupTopicAsync), [(nameof(group), group), (nameof(topic), topic)], cancellationToken);

	/// <inheritdoc/>
	public Task SetGroupAnnounceAsync(Jid group, bool announce, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SetGroupAnnounceAsync), [(nameof(group), group), (nameof(announce), announce)], cancellationToken);

	/// <inheritdoc/>
	public Task SetGroupLockedAsync(Jid group, bool locked, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(SetGroupLockedAsync), [(nameof(group), group), (nameof(locked), locked)], cancellationToken);

	/// <inheritdoc/>
	public Task<Uri> GetGroupInviteLinkAsync(Jid group, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(GetGroupInviteLinkAsync), [(nameof(group), group)], NewInviteLink, cancellationToken);

	/// <inheritdoc/>
	public Task<Uri> ResetGroupInviteLinkAsync(Jid group, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(ResetGroupInviteLinkAsync), [(nameof(group), group)], NewInviteLink, cancellationToken);

	/// <inheritdoc/>
	public Task<Jid> JoinGroupWithLinkAsync(Uri link, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(link);
		return CallAsync(nameof(JoinGroupWithLinkAsync), [(nameof(link), link)], NewGroup, cancellationToken);
	}

	/// <inheritdoc/>
	public Task LeaveGroupAsync(Jid group, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(LeaveGroupAsync), [(nameof(group), group)], cancellationToken);

	/// <inheritdoc/>
	public Task MuteChatAsync(Jid chat, DateTimeOffset? until = null, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(MuteChatAsync), [(nameof(chat), chat), (nameof(until), until)], cancellationToken);

	/// <inheritdoc/>
	public Task UnmuteChatAsync(Jid chat, CancellationToken cancellationToken = default) => CallAsync(nameof(UnmuteChatAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task PinChatAsync(Jid chat, CancellationToken cancellationToken = default) => CallAsync(nameof(PinChatAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task UnpinChatAsync(Jid chat, CancellationToken cancellationToken = default) => CallAsync(nameof(UnpinChatAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task ArchiveChatAsync(Jid chat, CancellationToken cancellationToken = default) => CallAsync(nameof(ArchiveChatAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task UnarchiveChatAsync(Jid chat, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(UnarchiveChatAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task MarkChatReadAsync(Jid chat, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(MarkChatReadAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task MarkChatUnreadAsync(Jid chat, CancellationToken cancellationToken = default) =>
		CallAsync(nameof(MarkChatUnreadAsync), [(nameof(chat), chat)], cancellationToken);

	/// <inheritdoc/>
	public Task StarMessageAsync(MessageId id, MessageSource source, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		return CallAsync(nameof(StarMessageAsync), [(nameof(id), id), (nameof(source), source)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task UnstarMessageAsync(MessageId id, MessageSource source, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		return CallAsync(nameof(UnstarMessageAsync), [(nameof(id), id), (nameof(source), source)], cancellationToken);
	}

	/// <inheritdoc/>
	public Task RequestHistoryAsync(MessageId oldest, MessageSource source, DateTimeOffset oldestTimestamp, int count = 50, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
		return CallAsync(
			nameof(RequestHistoryAsync),
			[(nameof(oldest), oldest), (nameof(source), source), (nameof(oldestTimestamp), oldestTimestamp), (nameof(count), count)],
			cancellationToken
		);
	}

	private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

	/// <summary>Throws what <see cref="FailNext"/> gave, if anything.</summary>
	private static void Fail(Exception? failure)
	{
		if (failure is not null)
			ExceptionDispatchInfo.Throw(failure);
	}

	private static ImmutableArray<UserInfo> ToUserInfo(ImmutableArray<Jid> users) =>
		[
			.. users.Select(static user => new UserInfo
			{
				Jid = user,
				About = null,
				VerifiedBusinessName = null,
				PictureId = null,
			}),
		];

	private static ImmutableArray<ParticipantResult> Done(ImmutableArray<Jid> users) =>
		[.. users.Select(static user => new ParticipantResult { Jid = user, JidAlt = null, Outcome = new ParticipantOutcome.Done() })];

	private static GroupInfo ToGroup(Jid group, string name, DateTimeOffset? createdAt, ImmutableArray<Jid> participants) =>
		new()
		{
			Jid = group,
			Name = name,
			Topic = null,
			Owner = null,
			OwnerAlt = null,
			CreatedAt = createdAt,
			IsAnnounce = false,
			IsLocked = false,
			Participants = [.. participants.Select(static user => new GroupParticipant { Jid = user, JidAlt = null, Role = GroupRole.Member })],
		};

	/// <summary>The next number of what the fake makes: groups, invite links, media, pairing codes.</summary>
	private long Make() => Interlocked.Increment(ref _made);

	private Jid NewGroup() => new() { User = (FirstGroup + Make() - 1).ToString(CultureInfo.InvariantCulture), Server = "g.us" };

	private Uri NewInviteLink() => new(Invariant($"https://chat.whatsapp.com/FakeInvite{Make()}"));

	private MediaRef ToRef(MediaType type, ReadOnlySpan<byte> content)
	{
		var made = Make();
		var key = SHA256.HashData(Encoding.UTF8.GetBytes(Invariant($"media key {made}")));
		var path = Invariant($"/v/fake/{made}");
		return new MediaRef
		{
			Type = type,
			DirectPath = path,
			Url = new Uri("https://mmg.whatsapp.net" + path),
			MediaKey = [.. key],
			FileSha256 = [.. SHA256.HashData(content)],
			FileEncSha256 = [.. SHA256.HashData([.. key, .. content])],
			FileLength = content.Length,
			StreamingSidecar = [],
		};
	}

	private Task<SendResult> SendAsync(string member, ReadOnlySpan<(string Name, object? Value)> arguments, Jid chat, MessageId? id, CancellationToken cancellationToken) =>
		CallAsync(
			member,
			arguments,
			() => new SendResult
			{
				Id = id ?? MessageId.New(),
				Chat = chat,
				Timestamp = _time.GetUtcNow(),
			},
			cancellationToken
		);

	/// <summary>A media send with options, recorded as the overload without options records one, with the options after the metadata.</summary>
	private Task<SendResult> SendMediaAsync(
		string member,
		Jid chat,
		(string Name, MediaRef? Value) media,
		object? metadata,
		SendOptions options,
		MessageId? id,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(media.Value, media.Name);
		ArgumentNullException.ThrowIfNull(metadata);
		ArgumentNullException.ThrowIfNull(options);
		return SendAsync(member, [(nameof(chat), chat), (media.Name, media.Value), (nameof(metadata), metadata), (nameof(options), options), (nameof(id), id)], chat, id, cancellationToken);
	}

	private Task<ImmutableArray<ParticipantResult>> ParticipantsAsync(string member, Jid group, IReadOnlyList<Jid> users, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(users);
		ImmutableArray<Jid> copy = [.. users];
		return CallAsync(member, [(nameof(group), group), (nameof(users), copy)], () => Done(copy), cancellationToken);
	}

	/// <summary>
	/// The pairing stream: it is recorded, and fails, when it is first read, as a call that opens a stream does; it stops on
	/// the call's token or the one given through <c>WithCancellation</c>.
	/// </summary>
#pragma warning disable IDE0390 // Justification: an async iterator awaits nothing here, and only one gets the WithCancellation token ([EnumeratorCancellation])
	private async IAsyncEnumerable<PairingUpdate> PairAsync(
		string member,
		(string Name, object? Value)[] arguments,
		Func<PairingUpdate> code,
		[EnumeratorCancellation] CancellationToken cancellationToken
	)
#pragma warning restore IDE0390
	{
		Begin(cancellationToken);
		Fail(Record(member, arguments));
		yield return code();
		cancellationToken.ThrowIfCancellationRequested();
		yield return new PairingUpdate.Paired();
	}

	/// <summary>A call can start: the fake is not disposed, and the token is not cancelled.</summary>
	private void Begin(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>Records the call; it fails with what <see cref="FailNext"/> gave, and does nothing when the fake is disposed or the token is cancelled.</summary>
	private Task CallAsync(string member, ReadOnlySpan<(string Name, object? Value)> arguments, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled(cancellationToken);

		return Record(member, arguments) is { } failure ? Task.FromException(failure) : Task.CompletedTask;
	}

	/// <summary>Records the call and returns <paramref name="result"/>'s, or what <see cref="FailNext"/> gave; nothing when the fake is disposed or the token is cancelled.</summary>
	private Task<T> CallAsync<T>(string member, ReadOnlySpan<(string Name, object? Value)> arguments, Func<T> result, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_pipeline.IsDisposed, this);
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled<T>(cancellationToken);

		return Record(member, arguments) is { } failure ? Task.FromException<T>(failure) : Task.FromResult(result());
	}

	/// <summary>Records the call, and takes the failure <see cref="FailNext"/> gave for it, if any.</summary>
	private Exception? Record(string member, ReadOnlySpan<(string Name, object? Value)> arguments)
	{
		var named = new Dictionary<string, object?>(arguments.Length, StringComparer.Ordinal);
		foreach (var (name, value) in arguments)
			named[name] = value;
		var call = new FakeCall
		{
			Member = member,
			Arguments = named.ToImmutableDictionary(StringComparer.Ordinal),
			Timestamp = _time.GetUtcNow(),
		};

		lock (_lock)
		{
			_calls.Add(call);
			return _failures.TryGetValue(member, out var queue) && queue.TryDequeue(out var failure) ? failure : null;
		}
	}

	/// <summary>The lane of an event whose handlers run, in its dispatcher, until they finished.</summary>
	private sealed class RunningLane(bool ephemeral, string lane)
	{
		public bool Ephemeral { get; } = ephemeral;

		public string Lane { get; } = lane;

		/// <summary>Work a handler started and that outlives it raises into the lane freely.</summary>
		public volatile bool Finished;
	}
}
