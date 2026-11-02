using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Contacts;
using Whatsmeow.Groups;
using Whatsmeow.Messaging;
using Whatsmeow.Pairing;
using Whatsmeow.Presence;
using Whatsmeow.Sessions;
using static Whatsmeow.Testing.Tests.Samples;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Testing.Tests;

/// <summary>What the code under test calls is recorded, returns a plausible result on the test's time, and fails only when told to (ADR 0036).</summary>
public sealed partial class CallTests : IAsyncDisposable
{
	/// <summary>Every member the fake records, in the order <see cref="EveryMember_IsRecordedInOrder"/> calls them.</summary>
	private static readonly string[] s_members =
	[
		nameof(IWhatsAppClient.UploadMediaAsync),
		nameof(IWhatsAppClient.ConnectAsync),
		nameof(IWhatsAppClient.DisconnectAsync),
		nameof(IWhatsAppClient.PairWithQrCodeAsync),
		nameof(IWhatsAppClient.PairWithPhoneNumberAsync),
		nameof(IWhatsAppClient.LogoutAsync),
		nameof(IWhatsAppClient.SendPresenceAsync),
		nameof(IWhatsAppClient.SubscribePresenceAsync),
		nameof(IWhatsAppClient.SendChatPresenceAsync),
		nameof(IWhatsAppClient.WatchStateAsync),
		nameof(IWhatsAppClient.UploadMediaFileAsync),
		nameof(IWhatsAppClient.DownloadMediaFileAsync),
		nameof(IWhatsAppClient.DownloadMediaAsync),
		nameof(IWhatsAppClient.RequestMediaRetryAsync),
		nameof(IWhatsAppClient.IsOnWhatsAppAsync),
		nameof(IWhatsAppClient.GetUserInfoAsync),
		nameof(IWhatsAppClient.GetProfilePictureAsync),
		nameof(IWhatsAppClient.ResolveJidsAsync),
		nameof(IWhatsAppClient.GetContactsAsync),
		nameof(IWhatsAppClient.GetBlocklistAsync),
		nameof(IWhatsAppClient.BlockAsync),
		nameof(IWhatsAppClient.UnblockAsync),
		nameof(IWhatsAppClient.SendTextAsync),
		nameof(IWhatsAppClient.SendImageAsync),
		nameof(IWhatsAppClient.SendVideoAsync),
		nameof(IWhatsAppClient.SendAudioAsync),
		nameof(IWhatsAppClient.SendDocumentAsync),
		nameof(IWhatsAppClient.SendStickerAsync),
		nameof(IWhatsAppClient.SendRawAsync),
		nameof(IWhatsAppClient.MarkReadAsync),
		nameof(IWhatsAppClient.GetGroupsAsync),
		nameof(IWhatsAppClient.GetGroupInfoAsync),
		nameof(IWhatsAppClient.CreateGroupAsync),
		nameof(IWhatsAppClient.AddParticipantsAsync),
		nameof(IWhatsAppClient.RemoveParticipantsAsync),
		nameof(IWhatsAppClient.PromoteParticipantsAsync),
		nameof(IWhatsAppClient.DemoteParticipantsAsync),
		nameof(IWhatsAppClient.SetGroupNameAsync),
		nameof(IWhatsAppClient.SetGroupTopicAsync),
		nameof(IWhatsAppClient.SetGroupAnnounceAsync),
		nameof(IWhatsAppClient.SetGroupLockedAsync),
		nameof(IWhatsAppClient.GetGroupInviteLinkAsync),
		nameof(IWhatsAppClient.ResetGroupInviteLinkAsync),
		nameof(IWhatsAppClient.JoinGroupWithLinkAsync),
		nameof(IWhatsAppClient.LeaveGroupAsync),
		nameof(IWhatsAppClient.MuteChatAsync),
		nameof(IWhatsAppClient.UnmuteChatAsync),
		nameof(IWhatsAppClient.PinChatAsync),
		nameof(IWhatsAppClient.UnpinChatAsync),
		nameof(IWhatsAppClient.ArchiveChatAsync),
		nameof(IWhatsAppClient.UnarchiveChatAsync),
		nameof(IWhatsAppClient.MarkChatReadAsync),
		nameof(IWhatsAppClient.MarkChatUnreadAsync),
		nameof(IWhatsAppClient.StarMessageAsync),
		nameof(IWhatsAppClient.UnstarMessageAsync),
	];

	private readonly FakeTimeProvider _time = new(Now);
	private readonly FakeWhatsAppClient _fake;

	public CallTests() => _fake = new FakeWhatsAppClient(Session, _time);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task SendText_ReportsTheRequestedChatTheClientIdAndTheTestsTime()
	{
		var id = MessageId.From("3EB0C0FFEE0000000000AA");
		_time.Advance(TimeSpan.FromMinutes(5));

		var sent = await _fake.SendTextAsync(Ana, "hello", id, TestContext.Current.CancellationToken);

		var at = Now.AddMinutes(5);
		Assert.Equal(new SendResult { Id = id, Chat = Ana, Timestamp = at }, sent);
		var call = Assert.Single(_fake.Calls);
		Assert.Equal(nameof(IWhatsAppClient.SendTextAsync), call.Member);
		Assert.Equal(at, call.Timestamp);
		Assert.Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["text"] = "hello", ["id"] = id }, call.Arguments);
	}

	[Fact(Timeout = 10_000)]
	public async Task SendWithoutAnId_GetsANewClientIdOfTheLibrarysForm()
	{
		var sent = await _fake.SendTextAsync(Ana, "hello", cancellationToken: TestContext.Current.CancellationToken);

		Assert.Matches(ClientId(), sent.Id.Value);
		Assert.Null(Assert.Single(_fake.Calls).Arguments["id"]);
	}

	/// <summary>Every member but the event ones, in the order called.</summary>
	[Fact(Timeout = 10_000)]
	public async Task EveryMember_IsRecordedInOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = _fake;
		var media = await client.UploadMediaAsync(new MemoryStream([1, 2, 3]), MediaType.Image, ct);
		var source = Source(Ana);
		var id = MessageId.New();

		await client.ConnectAsync(ct);
		await client.DisconnectAsync(ct);
		_ = await client.PairWithQrCodeAsync(ct).ToArrayAsync(ct);
		_ = await client.PairWithPhoneNumberAsync("5511999990001", ct).ToArrayAsync(ct);
		await client.LogoutAsync(ct);
		await client.SendPresenceAsync(PresenceState.Available, ct);
		await client.SubscribePresenceAsync(Ana, ct);
		await client.SendChatPresenceAsync(Ana, ChatPresenceState.Typing, ct);
		_ = await client.WatchStateAsync(ct).FirstAsync(ct);
		var file = Path.Combine(Directory.CreateTempSubdirectory("wmn-fake-").FullName, "media.bin");
		await File.WriteAllBytesAsync(file, [4, 5], ct);
		_ = await client.UploadMediaFileAsync(file, MediaType.Document, ct);
		await client.DownloadMediaFileAsync(media, file, ct);
		await client.DownloadMediaAsync(media, new MemoryStream(), ct);
		await client.RequestMediaRetryAsync(id, source, media, ct);
		_ = await client.IsOnWhatsAppAsync(["+55 11 99999-0001"], ct);
		_ = await client.GetUserInfoAsync([Ana], ct);
		_ = await client.GetProfilePictureAsync(Ana, ct);
		_ = await client.ResolveJidsAsync([Ana], ct);
		_ = await client.GetContactsAsync(ct);
		_ = await client.GetBlocklistAsync(ct);
		await client.BlockAsync(Bia, ct);
		await client.UnblockAsync(Bia, ct);
		_ = await client.SendTextAsync(Ana, "hi", cancellationToken: ct);
		_ = await client.SendImageAsync(Ana, media, new ImageMetadata { Caption = "image" }, cancellationToken: ct);
		_ = await client.SendVideoAsync(Ana, media, new VideoMetadata(), cancellationToken: ct);
		_ = await client.SendAudioAsync(Ana, media, new AudioMetadata { IsVoiceNote = true }, cancellationToken: ct);
		_ = await client.SendDocumentAsync(Ana, media, new DocumentMetadata { FileName = "a.pdf" }, cancellationToken: ct);
		_ = await client.SendStickerAsync(Ana, media, new StickerMetadata(), cancellationToken: ct);
#pragma warning disable WMN1001 // Justification: the raw send is a member like the others
		_ = await client.SendRawAsync(Ana, new E2E.Message { Conversation = "raw" }, cancellationToken: ct);
#pragma warning restore WMN1001
		await client.MarkReadAsync(Ana, [id], cancellationToken: ct);
		_ = await client.GetGroupsAsync(ct);
		_ = await client.GetGroupInfoAsync(Team, ct);
		_ = await client.CreateGroupAsync("team", [Ana, Bia], ct);
		_ = await client.AddParticipantsAsync(Team, [Ana], ct);
		_ = await client.RemoveParticipantsAsync(Team, [Ana], ct);
		_ = await client.PromoteParticipantsAsync(Team, [Bia], ct);
		_ = await client.DemoteParticipantsAsync(Team, [Bia], ct);
		await client.SetGroupNameAsync(Team, "renamed", ct);
		await client.SetGroupTopicAsync(Team, topic: null, ct);
		await client.SetGroupAnnounceAsync(Team, announce: true, ct);
		await client.SetGroupLockedAsync(Team, locked: true, ct);
		_ = await client.GetGroupInviteLinkAsync(Team, ct);
		_ = await client.ResetGroupInviteLinkAsync(Team, ct);
		_ = await client.JoinGroupWithLinkAsync(new Uri("https://chat.whatsapp.com/AbCdEfGhIjKlMnOpQrStUv"), ct);
		await client.LeaveGroupAsync(Team, ct);
		await client.MuteChatAsync(Ana, Now.AddHours(8), ct);
		await client.UnmuteChatAsync(Ana, ct);
		await client.PinChatAsync(Ana, ct);
		await client.UnpinChatAsync(Ana, ct);
		await client.ArchiveChatAsync(Ana, ct);
		await client.UnarchiveChatAsync(Ana, ct);
		await client.MarkChatReadAsync(Ana, ct);
		await client.MarkChatUnreadAsync(Ana, ct);
		await client.StarMessageAsync(id, source, ct);
		await client.UnstarMessageAsync(id, source, ct);

		Assert.Equal(
			s_members,
			_fake.Calls.Select(static call => call.Member),
			StringComparer.Ordinal
		);
		Assert.Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["messageIds"] = ImmutableArray.Create(id), ["sender"] = null }, _fake.Calls[29].Arguments);
		Assert.Equal(ImmutableArray.Create<byte>(1, 2, 3), _fake.Calls[0].Arguments["content"]);
	}

	[Fact(Timeout = 10_000)]
	public async Task RequestHistory_IsRecordedWithItsArguments_AndRefusesACountBelowOne()
	{
		var ct = TestContext.Current.CancellationToken;
		var id = MessageId.From("3EB0C0FFEE0000000000AB");

		await _fake.RequestHistoryAsync(id, Source(Ana), Now, cancellationToken: ct);
		_ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _fake.RequestHistoryAsync(id, Source(Ana), Now, 0, ct));

		var call = Assert.Single(_fake.Calls);
		Assert.Equal(nameof(IWhatsAppClient.RequestHistoryAsync), call.Member);
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["oldest"] = id, ["source"] = Source(Ana), ["oldestTimestamp"] = Now, ["count"] = 50 },
			call.Arguments
		);
	}

	[Fact(Timeout = 10_000)]
	public async Task Results_ArePlausible()
	{
		var ct = TestContext.Current.CancellationToken;
		byte[] content = [.. Encoding.UTF8.GetBytes("a picture")];

		var media = await _fake.UploadMediaAsync(new MemoryStream(content), MediaType.Image, ct);
		var created = await _fake.CreateGroupAsync("team", [Ana, Bia], ct);
		var joined = await _fake.JoinGroupWithLinkAsync(new Uri("https://chat.whatsapp.com/AbCdEfGhIjKlMnOpQrStUv"), ct);
		var link = await _fake.GetGroupInviteLinkAsync(Team, ct);
		var reset = await _fake.ResetGroupInviteLinkAsync(Team, ct);

		Assert.Equal((MediaType.Image, content.Length), (media.Type, media.FileLength));
		Assert.Equal(SHA256.HashData(content), media.FileSha256);
		Assert.Equal(32, media.MediaKey.Length);
		Assert.Equal(("g.us", "g.us"), (created.Group.Jid.Server, joined.Server));
		Assert.NotEqual(created.Group.Jid, joined);
		Assert.NotEqual(link, reset);
		ImmutableArray<ParticipantResult> done =
		[
			new() { Jid = Ana, JidAlt = null, Outcome = new ParticipantOutcome.Done() },
			new() { Jid = Bia, JidAlt = null, Outcome = new ParticipantOutcome.Done() },
		];
		Assert.Equal(
			new CreatedGroup
			{
				Group = new GroupInfo
				{
					Jid = created.Group.Jid,
					Name = "team",
					Topic = null,
					Owner = null,
					OwnerAlt = null,
					CreatedAt = Now,
					IsAnnounce = false,
					IsLocked = false,
					Participants =
					[
						new() { Jid = Ana, JidAlt = null, Role = GroupRole.Member },
						new() { Jid = Bia, JidAlt = null, Role = GroupRole.Member },
					],
				},
				Participants = done,
			},
			created
		);
		Assert.Equal(done, await _fake.AddParticipantsAsync(Team, [Ana, Bia], ct));
		Assert.Equal(
			[
				new PhoneNumberCheck
				{
					PhoneNumber = "+55 11 99999-0001",
					IsOnWhatsApp = true,
					Jid = Ana,
					Lid = null,
				},
			],
			await _fake.IsOnWhatsAppAsync(["+55 11 99999-0001"], ct)
		);
		Assert.Equal(
			[
				new UserInfo
				{
					Jid = Ana,
					About = null,
					VerifiedBusinessName = null,
					PictureId = null,
				},
			],
			await _fake.GetUserInfoAsync([Ana], ct)
		);
		Assert.Equal([new ResolvedJid { Jid = Ana, JidAlt = null }], await _fake.ResolveJidsAsync([Ana], ct));
		Assert.Equal(new ProfilePicture.None(), await _fake.GetProfilePictureAsync(Ana, ct));
		Assert.Empty(await _fake.GetContactsAsync(ct));
		Assert.Empty(await _fake.GetBlocklistAsync(ct));
		Assert.Empty(await _fake.GetGroupsAsync(ct));
		var pairing = await _fake.PairWithQrCodeAsync(ct).ToArrayAsync(ct);
		Assert.Equal(2, pairing.Length);
		_ = Assert.IsType<PairingUpdate.QrCode>(pairing[0]);
		Assert.Equal(new PairingUpdate.Paired(), pairing[1]);
		Assert.Equal(new SessionState.Connected(), await _fake.WatchStateAsync(ct).FirstAsync(ct));
	}

	[Fact(Timeout = 10_000)]
	public async Task FailNext_ThrowsTheGivenExceptionOnce_OnThatMemberOnly()
	{
		var ct = TestContext.Current.CancellationToken;
		var notOnWhatsApp = new RecipientNotFoundException("not on WhatsApp");
		_fake.FailNext(nameof(IWhatsAppClient.SendTextAsync), notOnWhatsApp);

		var media = await _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct);
		_ = await _fake.SendImageAsync(Ana, media, new ImageMetadata(), cancellationToken: ct);
		var thrown = await Assert.ThrowsAsync<RecipientNotFoundException>(() => _fake.SendTextAsync(Ana, "first", cancellationToken: ct));
		var second = await _fake.SendTextAsync(Ana, "second", cancellationToken: ct);

		Assert.Same(notOnWhatsApp, thrown);
		Assert.Equal(Ana, second.Chat);
		Assert.Equal(
			[nameof(IWhatsAppClient.UploadMediaAsync), nameof(IWhatsAppClient.SendImageAsync), nameof(IWhatsAppClient.SendTextAsync), nameof(IWhatsAppClient.SendTextAsync)],
			_fake.Calls.Select(static call => call.Member),
			StringComparer.Ordinal
		);
	}

	[Fact(Timeout = 10_000)]
	public async Task FailNext_InTheOrderGiven_WithWhatTheLibrarysExceptionsCarry()
	{
		var ct = TestContext.Current.CancellationToken;
		var link = new Uri("https://chat.whatsapp.com/AbCdEfGhIjKlMnOpQrStUv");
		_fake.FailNext(nameof(IWhatsAppClient.JoinGroupWithLinkAsync), new WhatsmeowException("not a link") { ErrorCode = WhatsmeowErrorCode.InviteLinkInvalid });
		_fake.FailNext(nameof(IWhatsAppClient.JoinGroupWithLinkAsync), new WhatsAppErrorException("full") { Code = 409 });
		_fake.FailNext(nameof(IWhatsAppClient.JoinGroupWithLinkAsync), new TemporarilyBannedException("banned") { Until = Now.AddDays(1) });

		var invalid = await Assert.ThrowsAsync<WhatsmeowException>(() => _fake.JoinGroupWithLinkAsync(link, ct));
		var full = await Assert.ThrowsAsync<WhatsAppErrorException>(() => _fake.JoinGroupWithLinkAsync(link, ct));
		var banned = await Assert.ThrowsAsync<TemporarilyBannedException>(() => _fake.JoinGroupWithLinkAsync(link, ct));

		Assert.Equal(WhatsmeowErrorCode.InviteLinkInvalid, invalid.ErrorCode);
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 409), (full.ErrorCode, full.Code));
		Assert.Equal((WhatsmeowErrorCode.TemporarilyBanned, Now.AddDays(1)), (banned.ErrorCode, banned.Until));
		Assert.Equal("g.us", (await _fake.JoinGroupWithLinkAsync(link, ct)).Server);
	}

	[Fact(Timeout = 10_000)]
	public async Task FailNext_OnAStream_ThrowsWhenItIsFirstRead()
	{
		var ct = TestContext.Current.CancellationToken;
		var refused = new PairingException("already paired") { ErrorCode = WhatsmeowErrorCode.AlreadyPaired };
		_fake.FailNext(nameof(IWhatsAppClient.PairWithQrCodeAsync), refused);

		var pairing = _fake.PairWithQrCodeAsync(ct);
		Assert.Empty(_fake.Calls);

		Assert.Same(refused, await Assert.ThrowsAsync<PairingException>(async () => await pairing.ToArrayAsync(ct).ConfigureAwait(false)));
		Assert.Equal(nameof(IWhatsAppClient.PairWithQrCodeAsync), Assert.Single(_fake.Calls).Member);
	}

	[Fact(Timeout = 10_000)]
	public async Task CallWithACancelledToken_Throws_AndIsNotRecorded()
	{
		using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		await cancelled.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fake.SendTextAsync(Ana, "never", cancellationToken: cancelled.Token));

		Assert.Empty(_fake.Calls);
	}

	[Theory]
	[InlineData("On")]
	[InlineData("WaitForAsync")]
	[InlineData("OnHandlerError")]
	[InlineData("RaiseAsync")]
	[InlineData("FailNext")]
	[InlineData("DisposeAsync")]
	[InlineData("SendText")]
	[InlineData("sendTextAsync")]
	public async Task FailNext_OfAMemberThatIsNoCall_IsRefused(string member)
	{
		var refused = Assert.Throws<ArgumentException>(() => _fake.FailNext(member, new InvalidOperationException("never")));

		Assert.Equal("member", refused.ParamName);
		Assert.Contains(member, refused.Message, StringComparison.Ordinal);
		_ = await _fake.SendTextAsync(Ana, "not failed", cancellationToken: TestContext.Current.CancellationToken);
	}

	[Fact]
	public void FailNext_TakesEveryMemberTheFakeRecords()
	{
		foreach (var member in s_members)
			_fake.FailNext(member, new InvalidOperationException(member));
	}

	[Fact(Timeout = 10_000)]
	public async Task UploadOfAMissingFile_IsRecorded_AndTakesItsFailureFirst()
	{
		var ct = TestContext.Current.CancellationToken;
		var missing = Path.Combine(Path.GetTempPath(), $"wmn-fake-missing-{Guid.NewGuid():N}.bin");
		var refused = new MediaExpiredException("refused");
		_fake.FailNext(nameof(IWhatsAppClient.UploadMediaFileAsync), refused);

		Assert.Same(refused, await Assert.ThrowsAsync<MediaExpiredException>(() => _fake.UploadMediaFileAsync(missing, MediaType.Image, ct)));
		_ = await Assert.ThrowsAsync<FileNotFoundException>(() => _fake.UploadMediaFileAsync(missing, MediaType.Image, ct));

		var arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = missing, ["type"] = MediaType.Image };
		Assert.Equal(
			[new FakeCall { Member = nameof(IWhatsAppClient.UploadMediaFileAsync), Arguments = arguments, Timestamp = Now }, new FakeCall { Member = nameof(IWhatsAppClient.UploadMediaFileAsync), Arguments = arguments, Timestamp = Now }],
			_fake.Calls
		);
	}

	[Fact(Timeout = 10_000)]
	public async Task Disposed_EveryCallThrowsObjectDisposed_AndIsNotRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var file = Path.Combine(Directory.CreateTempSubdirectory("wmn-fake-").FullName, "media.bin");
		await File.WriteAllBytesAsync(file, [1], ct);
		var media = await _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct);
		await _fake.DisposeAsync();

		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.SendTextAsync(Ana, "late", cancellationToken: ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.ConnectAsync(ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.GetGroupsAsync(ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.UploadMediaFileAsync(file, MediaType.Image, ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => _fake.DownloadMediaFileAsync(media, file, ct));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await _fake.PairWithQrCodeAsync(ct).ToArrayAsync(ct).ConfigureAwait(false));
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await _fake.WatchStateAsync(ct).FirstAsync(ct).ConfigureAwait(false));

		Assert.Equal(nameof(IWhatsAppClient.UploadMediaAsync), Assert.Single(_fake.Calls).Member);
	}

	[Fact(Timeout = 10_000)]
	public async Task Pairing_HonoursTheTokenGivenThroughWithCancellation()
	{
		var ct = TestContext.Current.CancellationToken;
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
		await using (var updates = _fake.PairWithQrCodeAsync(ct).WithCancellation(stop.Token).GetAsyncEnumerator())
		{
			Assert.True(await updates.MoveNextAsync());
			_ = Assert.IsType<PairingUpdate.QrCode>(updates.Current);

			await stop.CancelAsync();

			_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await updates.MoveNextAsync());
		}

		using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
		await cancelled.CancelAsync();
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await _fake.PairWithPhoneNumberAsync("5511999990001", ct).WithCancellation(cancelled.Token).GetAsyncEnumerator().MoveNextAsync()
		);
		Assert.Equal(nameof(IWhatsAppClient.PairWithQrCodeAsync), Assert.Single(_fake.Calls).Member);
	}

	[Fact(Timeout = 10_000)]
	public async Task Call_EqualsACallBuiltWithTheSameMemberArgumentsAndTime()
	{
		var id = MessageId.From("3EB0C0FFEE0000000000AA");
		_ = await _fake.SendTextAsync(Ana, "hello", id, TestContext.Current.CancellationToken);
		var actual = Assert.Single(_fake.Calls);

		var expected = new FakeCall
		{
			Member = nameof(IWhatsAppClient.SendTextAsync),
			Arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["text"] = "hello", ["id"] = id },
			Timestamp = Now,
		};

		Assert.Equal(expected, actual);
		Assert.Equal(expected.GetHashCode(), actual.GetHashCode());
		Assert.NotEqual(expected with { Arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["text"] = "bye", ["id"] = id } }, actual);
		Assert.NotEqual(expected with { Arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["text"] = "hello" } }, actual);
		Assert.NotEqual(expected with { Arguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["text"] = "hello", ["sender"] = id } }, actual);
		Assert.NotEqual(expected with { Member = nameof(IWhatsAppClient.SendRawAsync) }, actual);
		Assert.NotEqual(expected with { Timestamp = Now.AddSeconds(1) }, actual);
	}

	[GeneratedRegex("^3EB0[0-9A-F]{18}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ClientId();
}
