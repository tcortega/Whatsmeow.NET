using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageKey = Whatsmeow.Messaging.MessageKey;
using MessageSource = Whatsmeow.Events.MessageSource;
using ProtocolType = Whatsmeow.Proto.WAWebProtobufsE2E.ProtocolMessage.Types.Type;
using WACommon = Whatsmeow.Proto.WACommon;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// Reactions, edits, revokes, replies and mentions through the public client, on the shared fake-mode sidecar, and what
/// whatsmeow's unwrapping of a received message said: ephemeral, the chat a message from the account's phone went to,
/// and the message as received. What WhatsApp got is read from the fake's log by condition; "nothing was sent" shows by
/// order.
/// </summary>
public sealed class MessageActionTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private static readonly Jid s_peer = Jid.Parse("900000301@lid");
	private static readonly Jid s_group = Jid.Parse("120363000000000301@g.us");
	private static readonly Jid s_member = Jid.Parse("900000302@lid");
	private static readonly Jid s_admin = Jid.Parse("900000303@lid");

	[Fact(Timeout = Timeout)]
	public async Task React_WithAnEmojiThenWithNone_WhatsAppGetsTheReactionThenItsRemoval_AndTheResultIsAsForText()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-react";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var id = MessageId.New();

		var result = await client.SendReactionAsync(s_group, new MessageKey { Id = MessageId.From("T1"), Sender = s_member }, "👍", id, ct);
		_ = await client.SendReactionAsync(s_peer, new MessageKey { Id = MessageId.From("T2") }, "", cancellationToken: ct);

		var sent = await NextAsync(Session, 0, 2, ct);
		Assert.Equal(
			new E2E.Message
			{
				ReactionMessage = new E2E.ReactionMessage
				{
					Key = new WACommon.MessageKey { RemoteJID = s_group.ToString(), FromMe = false, ID = "T1", Participant = s_member.ToString() },
					Text = "👍",
					SenderTimestampMS = sent[0].Message.Message.ReactionMessage.SenderTimestampMS,
				},
			},
			sent[0].Message.Message
		);
		Assert.Equal(new SendResult { Id = id, Chat = s_group, Timestamp = sent[0].Message.MessageTime.ToDateTimeOffset() }, result);

		// The account's own message, with no sender; an empty emoji removes the reaction.
		Assert.Equal(
			new E2E.Message
			{
				ReactionMessage = new E2E.ReactionMessage
				{
					Key = new WACommon.MessageKey { RemoteJID = s_peer.ToString(), FromMe = true, ID = "T2" },
					Text = "",
					SenderTimestampMS = sent[1].Message.Message.ReactionMessage.SenderTimestampMS,
				},
			},
			sent[1].Message.Message
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Edit_WhatsAppGetsTheNewTextAsAnEditOfTheMessage_AndTheResultIsAsForText()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-edit";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var id = MessageId.New();

		var result = await client.EditMessageAsync(s_peer, MessageId.From("T1"), "fixed", id, ct);

		var sent = (await NextAsync(Session, 0, 1, ct))[0].Message;
		Assert.Equal(
			new SentMessage
			{
				Id = id.Value,
				Chat = s_peer.ToString(),
				Message = new E2E.Message
				{
					EditedMessage = new E2E.FutureProofMessage
					{
						Message = new E2E.Message
						{
							ProtocolMessage = new E2E.ProtocolMessage
							{
								Key = new WACommon.MessageKey { RemoteJID = s_peer.ToString(), FromMe = true, ID = "T1" },
								Type = ProtocolType.MessageEdit,
								EditedMessage = new E2E.Message { Conversation = "fixed" },
								TimestampMS = sent.Message.EditedMessage.Message.ProtocolMessage.TimestampMS,
							},
						},
					},
				},
				MessageTime = sent.MessageTime,
			},
			sent
		);
		Assert.Equal(new SendResult { Id = id, Chat = s_peer, Timestamp = sent.MessageTime.ToDateTimeOffset() }, result);
	}

	[Fact(Timeout = Timeout)]
	public async Task Revoke_TheOwnMessageAndAsAdminAMembers_WhatsAppGetsBothRevokes()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-revoke";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);

		_ = await client.RevokeMessageAsync(s_group, new MessageKey { Id = MessageId.From("T1") }, cancellationToken: ct);
		_ = await client.RevokeMessageAsync(s_group, new MessageKey { Id = MessageId.From("T2"), Sender = s_member }, cancellationToken: ct);

		var sent = await NextAsync(Session, 0, 2, ct);
		Assert.Equal(Revoke(new WACommon.MessageKey { RemoteJID = s_group.ToString(), FromMe = true, ID = "T1" }), sent[0].Message.Message);
		Assert.Equal(
			Revoke(new WACommon.MessageKey { RemoteJID = s_group.ToString(), FromMe = false, ID = "T2", Participant = s_member.ToString() }),
			sent[1].Message.Message
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Revoke_RefusedByWhatsApp_ThrowsWhatsAppErrorWithItsCode_AndARepeatThrowsTheSameAndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-revoke-refused";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		_ = await fixture.Fake.RefuseNextSendAsync(new RefuseNextSendRequest { SessionId = Session, Code = 403 }, cancellationToken: ct);
		var target = new MessageKey { Id = MessageId.From("T1"), Sender = s_member };
		var id = MessageId.New();

		var refused = await Assert.ThrowsAsync<WhatsAppErrorException>(() => client.RevokeMessageAsync(s_group, target, id, ct));
		var again = await Assert.ThrowsAsync<WhatsAppErrorException>(() => client.RevokeMessageAsync(s_group, target, id, ct));

		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (refused.ErrorCode, refused.Code));
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (again.ErrorCode, again.Code));
		var first = (await NextAsync(Session, 0, 1, ct))[0];
		Assert.Equal(id.Value, first.Message.Id);
		var next = await client.SendTextAsync(s_group, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, first.Index, 1, ct))[0].Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AReactionAnEditAndARevoke_AreTheirContents_NamingTheTarget()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-receive";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);

		(Jid Chat, Jid Sender, E2E.Message Message, MessageContent Content)[] cases =
		[
			// A member reacts to another member's message, which the key names by its participant, without the device.
			(
				s_group,
				s_member,
				new() { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_group, fromMe: false, "T1", s_admin with { Device = 4 }), Text = "❤" } },
				new ReactionContent { Target = new MessageKey { Id = MessageId.From("T1"), Sender = s_admin }, Emoji = "❤" }
			),
			// A member removes a reaction to their own message.
			(
				s_group,
				s_member,
				new() { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_group, fromMe: true, "T2"), Text = "" } },
				new ReactionContent { Target = new MessageKey { Id = MessageId.From("T2"), Sender = s_member }, Emoji = "" }
			),
			// In a direct chat, the other side's message is the account's own: null.
			(
				s_peer,
				s_peer,
				new() { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_peer, fromMe: false, "T3"), Text = "👍" } },
				new ReactionContent { Target = new MessageKey { Id = MessageId.From("T3") }, Emoji = "👍" }
			),
			// An edit, in whatsmeow's wrapper, as whatsmeow builds one (send.go:616-635).
			(
				s_group,
				s_member,
				new()
				{
					EditedMessage = new E2E.FutureProofMessage
					{
						Message = new E2E.Message
						{
							ProtocolMessage = new E2E.ProtocolMessage
							{
								Key = Key(s_group, fromMe: true, "T4"),
								Type = ProtocolType.MessageEdit,
								EditedMessage = new E2E.Message { Conversation = "new" },
							},
						},
					},
				},
				new EditContent { Target = new MessageKey { Id = MessageId.From("T4"), Sender = s_member }, Content = new TextContent { Text = "new" } }
			),
			// The member's own revoke, and an admin's of the member's message: who deleted it is the source.
			(
				s_group,
				s_member,
				Revoke(Key(s_group, fromMe: true, "T5")),
				new RevokeContent { Target = new MessageKey { Id = MessageId.From("T5"), Sender = s_member } }
			),
			(
				s_group,
				s_admin,
				Revoke(Key(s_group, fromMe: false, "T6", s_member)),
				new RevokeContent { Target = new MessageKey { Id = MessageId.From("T6"), Sender = s_member } }
			),
			// The actor's own message, sent from one of its devices: the target names the user, not the device.
			(
				s_group,
				s_member with { Device = 7 },
				Revoke(Key(s_group, fromMe: true, "T9")),
				new RevokeContent { Target = new MessageKey { Id = MessageId.From("T9"), Sender = s_member } }
			),
			// In a group, a key that is not the actor's and names no participant names no message: malformed.
			(s_group, s_member, new() { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_group, fromMe: false, "T10"), Text = "👍" } }, new UnknownContent()),
			// A protocol message without a type is no revoke, though REVOKE is the type's zero value.
			(s_group, s_member, new() { ProtocolMessage = new E2E.ProtocolMessage { Key = Key(s_group, fromMe: true, "T7") } }, new UnknownContent()),
		];

		for (var i = 0; i < cases.Length; i++)
		{
			await DeliverAsync(Session, $"A{i}", cases[i].Chat, cases[i].Sender, cases[i].Message, ct);
			Assert.Equal(Received(Session, $"A{i}", cases[i].Chat, cases[i].Sender, cases[i].Content), await received.NextAsync(ct));
		}

		// The account's phone reacts in a direct chat to the other side's message, which is the chat's.
		var phone = Jid.Parse($"{Session}@s.whatsapp.net");
		await DeliverAsync(
			Session,
			"P1",
			s_peer,
			phone,
			new E2E.Message { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_peer, fromMe: false, "T8"), Text = "👍" } },
			ct,
			isFromMe: true
		);
		var own = Received(Session, "P1", s_peer, phone, new ReactionContent { Target = new MessageKey { Id = MessageId.From("T8"), Sender = s_peer }, Emoji = "👍" });
		Assert.Equal(own with { Source = own.Source with { IsFromMe = true } }, await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AnActionOnTheAccountsOwnMessage_NamesNoSender_HoweverItsKeyNamesTheAccount()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-receive-own";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var device = Jid.Parse(SessionSetup.Jid(Session));
		var phone = device with { Device = 0 };
		var own = new MessageKey { Id = MessageId.From("T1") };

		// The other side of a direct chat names the account by its device, a member by its phone number.
		await DeliverAsync(Session, "O1", s_peer, s_peer, new E2E.Message { ReactionMessage = new E2E.ReactionMessage { Key = Key(s_peer, fromMe: false, "T1", device), Text = "👍" } }, ct);
		Assert.Equal(Received(Session, "O1", s_peer, s_peer, new ReactionContent { Target = own, Emoji = "👍" }), await received.NextAsync(ct));
		await DeliverAsync(Session, "O2", s_group, s_member, Revoke(Key(s_group, fromMe: false, "T1", phone)), ct);
		Assert.Equal(Received(Session, "O2", s_group, s_member, new RevokeContent { Target = own }), await received.NextAsync(ct));

		// The account's phone revokes the account's message.
		await DeliverAsync(Session, "O3", s_peer, phone, Revoke(Key(s_peer, fromMe: true, "T1")), ct, isFromMe: true);
		var revoked = Received(Session, "O3", s_peer, phone, new RevokeContent { Target = own });
		Assert.Equal(revoked with { Source = revoked.Source with { IsFromMe = true } }, await received.NextAsync(ct));
	}

	/// <summary>
	/// Every kind of send carries the quote and the mentions to WhatsApp, and the same message received carries them to
	/// the application: the context is on the text's extended text, on the media message, and inside a captioned
	/// document's wrapper.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task SendAndReceive_EveryKindInReplyMentioningUsers_CarriesTheQuoteAndTheMentions()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-reply-kinds";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var image = await UploadAsync(client, ct);
		var upload = (await NextAsync(Session, 0, 1, ct))[0];
		var pn = Jid.Parse("5511900000304@s.whatsapp.net");
		var options = new SendOptions
		{
			ReplyTo = new MessageReply { Id = MessageId.From("Q1"), Sender = s_member, QuotedText = "the question" },
			Mentions = [s_member, pn],
		};
		var context = new E2E.ContextInfo
		{
			StanzaID = "Q1",
			Participant = s_member.ToString(),
			QuotedMessage = new E2E.Message { Conversation = "the question" },
			QuotedType = E2E.ContextInfo.Types.QuotedType.Explicit,
			MentionedJID = { s_member.ToString(), pn.ToString() },
		};

		(Func<Task<SendResult>> Send, Func<E2E.Message, E2E.ContextInfo?> Context)[] kinds =
		[
			(() => client.SendTextAsync(s_group, "@900000302 yes", options: options, cancellationToken: ct), static m => m.ExtendedTextMessage?.ContextInfo),
			(() => client.SendImageAsync(s_group, image, new ImageMetadata { Caption = "look" }, options: options, cancellationToken: ct), static m => m.ImageMessage?.ContextInfo),
			(() => client.SendVideoAsync(s_group, image with { Type = MediaType.Video }, new VideoMetadata { Caption = "watch" }, options: options, cancellationToken: ct), static m => m.VideoMessage?.ContextInfo),
			(
				() => client.SendAudioAsync(s_group, image with { Type = MediaType.Audio }, new AudioMetadata { IsVoiceNote = true }, options: options, cancellationToken: ct),
				static m => m.AudioMessage?.ContextInfo
			),
			(
				() => client.SendDocumentAsync(s_group, image with { Type = MediaType.Document }, new DocumentMetadata { Caption = "read" }, options: options, cancellationToken: ct),
				static m => m.DocumentWithCaptionMessage?.Message?.DocumentMessage?.ContextInfo
			),
			(() => client.SendStickerAsync(s_group, image, new StickerMetadata(), options: options, cancellationToken: ct), static m => m.StickerMessage?.ContextInfo),
		];

		var after = upload.Index;
		for (var i = 0; i < kinds.Length; i++)
		{
			_ = await kinds[i].Send();
			var sent = await NextMessageAsync(Session, after, ct);
			after = sent.Index;
			Assert.Equal(context, kinds[i].Context(sent.Message.Message));

			// The member gets it as sent, and so does the account, from the member's side.
			await DeliverAsync(Session, $"K{i}", s_group, s_member, sent.Message.Message, ct);
			var message = await received.NextAsync(ct);
			Assert.Equal(new QuotedMessage { Id = MessageId.From("Q1"), Sender = s_member, Content = new TextContent { Text = "the question" } }, message.ReplyTo);
			Assert.Equal<Jid>([s_member, pn], message.Mentions);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_WithDefaultMentionsAndNoReply_IsAPlainText()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-default-mentions";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);

		_ = await client.SendTextAsync(s_peer, "plain", options: new SendOptions { Mentions = default }, cancellationToken: ct);

		Assert.Equal(new E2E.Message { Conversation = "plain" }, (await NextAsync(Session, 0, 1, ct))[0].Message.Message);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_TextAndMediaInReplyMentioningUsers_CarryTheQuoteAndTheMentions()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-reply";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var image = await UploadAsync(client, ct);
		var upload = (await NextAsync(Session, 0, 1, ct))[0];
		var options = new SendOptions
		{
			ReplyTo = new MessageReply { Id = MessageId.From("Q1"), Sender = s_member, QuotedText = "the question" },
			Mentions = [s_member, Jid.Parse("5511900000304@s.whatsapp.net")],
		};
		var context = new E2E.ContextInfo
		{
			StanzaID = "Q1",
			Participant = s_member.ToString(),
			QuotedMessage = new E2E.Message { Conversation = "the question" },
			QuotedType = E2E.ContextInfo.Types.QuotedType.Explicit,
			MentionedJID = { s_member.ToString(), "5511900000304@s.whatsapp.net" },
		};

		_ = await client.SendTextAsync(s_group, "@900000302 yes", options: options, cancellationToken: ct);
		_ = await client.SendImageAsync(s_group, image, new ImageMetadata { Caption = "@900000302 look" }, options: options, cancellationToken: ct);
		_ = await client.SendDocumentAsync(s_group, image with { Type = MediaType.Document }, new DocumentMetadata(), options: options, cancellationToken: ct);

		var sent = await NextAsync(Session, upload.Index, 3, ct);
		Assert.Equal(new E2E.Message { ExtendedTextMessage = new E2E.ExtendedTextMessage { Text = "@900000302 yes", ContextInfo = context } }, sent[0].Message.Message);
		Assert.Equal(("@900000302 look", context), (sent[1].Message.Message.ImageMessage.Caption, sent[1].Message.Message.ImageMessage.ContextInfo));
		Assert.Equal(context, sent[2].Message.Message.DocumentMessage.ContextInfo);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_InReplyToTheAccountsOwnMessage_TheQuoteNamesTheAccount()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-reply-own";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);

		_ = await client.SendTextAsync(s_peer, "and more", options: new SendOptions { ReplyTo = new MessageReply { Id = MessageId.From("Q1") } }, cancellationToken: ct);

		Assert.Equal(
			new E2E.Message
			{
				ExtendedTextMessage = new E2E.ExtendedTextMessage
				{
					Text = "and more",
					ContextInfo = new E2E.ContextInfo
					{
						StanzaID = "Q1",
						Participant = $"{Session}@s.whatsapp.net",
						QuotedMessage = new E2E.Message { Conversation = "" },
						QuotedType = E2E.ContextInfo.Types.QuotedType.Explicit,
					},
				},
			},
			(await NextAsync(Session, 0, 1, ct))[0].Message.Message
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AReplyMentioningUsers_HasTheQuoteAndTheMentions()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-receive-reply";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var self = Jid.Parse($"{Session}@s.whatsapp.net");

		await DeliverAsync(
			Session,
			"R1",
			s_group,
			s_member,
			new E2E.Message
			{
				ExtendedTextMessage = new E2E.ExtendedTextMessage
				{
					Text = "@me yes",
					ContextInfo = new E2E.ContextInfo
					{
						StanzaID = "Q1",
						Participant = self.ToString(),
						QuotedMessage = new E2E.Message { Conversation = "the question" },
						MentionedJID = { self.ToString(), "not@a@jid" },
					},
				},
			},
			ct
		);
		Assert.Equal(
			Received(Session, "R1", s_group, s_member, new TextContent { Text = "@me yes" }) with
			{
				ReplyTo = new QuotedMessage { Id = MessageId.From("Q1"), Sender = null, Content = new TextContent { Text = "the question" } },
				Mentions = [self],
			},
			await received.NextAsync(ct)
		);

		// A quote WhatsApp sent without its content, on a media message.
		await DeliverAsync(
			Session,
			"R2",
			s_group,
			s_member,
			new E2E.Message { StickerMessage = new E2E.StickerMessage { ContextInfo = new E2E.ContextInfo { StanzaID = "Q2", Participant = s_admin.ToString() } } },
			ct
		);
		var sticker = await received.NextAsync(ct);
		Assert.Equal(new QuotedMessage { Id = MessageId.From("Q2"), Sender = s_admin, Content = null }, sticker.ReplyTo);
		Assert.Empty(sticker.Mentions);
	}

	/// <summary>
	/// A quote of the account's own message has no sender, however its context names the account, as a received action's
	/// target has none: null always means the account's. A quote that names no user, without a participant or with one
	/// that is not a user's JID, could only read as the account's, so the message is no reply.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Receive_AReplyToTheAccountsOwnMessage_NamesNoSender_HoweverItsContextNamesTheAccount()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-receive-own-quote";
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		var lid = Jid.Parse("900000399:1@lid");
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = Session, Jid = SessionSetup.Jid(Session), Lid = lid.ToString() }, cancellationToken: ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var device = Jid.Parse(SessionSetup.Jid(Session));
		var phone = device with { Device = 0 };
		var reply = static (string? participant) =>
			new E2E.Message
			{
				ExtendedTextMessage = new E2E.ExtendedTextMessage
				{
					Text = "yes",
					ContextInfo = participant is null
						? new E2E.ContextInfo { StanzaID = "Q1", QuotedMessage = new E2E.Message { Conversation = "mine" } }
						: new E2E.ContextInfo { StanzaID = "Q1", Participant = participant, QuotedMessage = new E2E.Message { Conversation = "mine" } },
				},
			};
		var text = new TextContent { Text = "yes" };

		// By the account's phone number or LID, from one of its devices, and in the legacy form.
		string[] own = [phone.ToString(), (device with { Device = 5 }).ToString(), $"{Session}@c.us", (lid with { Device = 0 }).ToString(), (lid with { Device = 4 }).ToString()];
		for (var i = 0; i < own.Length; i++)
		{
			await DeliverAsync(Session, $"Q{i}", s_group, s_member, reply(own[i]), ct);
			Assert.Equal(
				Received(Session, $"Q{i}", s_group, s_member, text) with
				{
					ReplyTo = new QuotedMessage { Id = MessageId.From("Q1"), Sender = null, Content = new TextContent { Text = "mine" } },
				},
				await received.NextAsync(ct)
			);
		}

		string?[] none = [null, "333@", s_group.ToString()];
		for (var i = 0; i < none.Length; i++)
		{
			await DeliverAsync(Session, $"N{i}", s_group, s_member, reply(none[i]), ct);
			Assert.Equal(Received(Session, $"N{i}", s_group, s_member, text), await received.NextAsync(ct));
		}
	}

	/// <summary>
	/// A received message prints every public member but the experimental <c>Raw</c>, whose text carries media keys: a
	/// member added later and left out of the hand-written printing fails here.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task Receive_AMessage_PrintsEveryMemberButRaw()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-print";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);

		await DeliverAsync(Session, "P1", s_peer, s_peer, new E2E.Message { Conversation = "hi" }, ct);
		var printed = (await received.NextAsync(ct)).ToString();

		var members = typeof(MessageReceived).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(static p => p.Name).ToArray();
		Assert.Contains("Raw", members, StringComparer.Ordinal);
		Assert.All(members.Where(static m => m != "Raw"), m => Assert.Matches($"[{{,] {m} = ", printed));
		Assert.DoesNotMatch("[{,] Raw = ", printed);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_ADisappearingMessage_SaysSo_ByItsWrapperOrItsExpiration()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-ephemeral";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var text = new E2E.Message { ExtendedTextMessage = new E2E.ExtendedTextMessage { Text = "soon gone" } };

		await DeliverAsync(Session, "E1", s_peer, s_peer, new E2E.Message { EphemeralMessage = new E2E.FutureProofMessage { Message = text } }, ct);
		await DeliverAsync(
			Session,
			"E2",
			s_peer,
			s_peer,
			new E2E.Message { ExtendedTextMessage = new E2E.ExtendedTextMessage { Text = "soon gone", ContextInfo = new E2E.ContextInfo { Expiration = 604_800 } } },
			ct
		);
		await DeliverAsync(Session, "E3", s_peer, s_peer, text, ct);

		var expected = Received(Session, "E1", s_peer, s_peer, new TextContent { Text = "soon gone" });
		Assert.Equal(expected with { IsEphemeral = true }, await received.NextAsync(ct));
		Assert.Equal(expected with { Id = MessageId.From("E2"), IsEphemeral = true }, await received.NextAsync(ct));
		Assert.Equal(expected with { Id = MessageId.From("E3") }, await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_FromTheAccountsPhone_CarriesTheChatItWentTo()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-device-sent";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var phone = Jid.Parse($"{Session}@s.whatsapp.net");

		await DeliverAsync(
			Session,
			"D1",
			s_peer,
			phone,
			new E2E.Message
			{
				DeviceSentMessage = new E2E.DeviceSentMessage { DestinationJID = s_peer.ToString(), Message = new E2E.Message { Conversation = "from the phone" } },
			},
			ct,
			isFromMe: true
		);

		var expected = Received(Session, "D1", s_peer, phone, new TextContent { Text = "from the phone" });
		Assert.Equal(expected with { Source = expected.Source with { IsFromMe = true }, DestinationChat = s_peer }, await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AWrappedMessage_RawIsTheMessageAsReceived()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-raw";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var wrapped = new E2E.Message
		{
			EphemeralMessage = new E2E.FutureProofMessage
			{
				Message = new E2E.Message { ViewOnceMessageV2 = new E2E.FutureProofMessage { Message = new E2E.Message { LocationMessage = new E2E.LocationMessage { DegreesLatitude = 1 } } } },
			},
		};
		var plain = new E2E.Message { LocationMessage = new E2E.LocationMessage { DegreesLatitude = 2 } };

		await DeliverAsync(Session, "W1", s_peer, s_peer, wrapped, ct);
		await DeliverAsync(Session, "W2", s_peer, s_peer, plain, ct);

		var first = await received.NextAsync(ct);
#pragma warning disable WMN1001 // Justification: the raw message is what is under test
		Assert.Equal(wrapped, first.Raw);
		Assert.Equal(plain, (await received.NextAsync(ct)).Raw);
#pragma warning restore WMN1001

		// Not printed: protobuf's text of a message carries its media keys.
		Assert.DoesNotContain("Raw", first.ToString(), StringComparison.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task ReadSelfReceipt_InAGroup_CarriesTheSenderOfItsMessages()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-read-self";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var receipts = new Received<ReceiptReceived>(client);
		await ConnectAsync(client, ct);
		var phone = Jid.Parse($"{Session}@s.whatsapp.net");

		_ = await fixture.Fake.DeliverReceiptAsync(
			new DeliverReceiptRequest
			{
				SessionId = Session,
				Chat = s_group.ToString(),
				Sender = phone.ToString(),
				IsFromMe = true,
				MessageIds = { "M1" },
				Type = "read-self",
				ReceiptTime = Timestamp.FromDateTimeOffset(Deliveries.At),
				MessageSender = s_member.ToString(),
			},
			cancellationToken: ct
		);

		Assert.Equal(
			new ReceiptReceived
			{
				SessionId = SessionId.From(Session),
				Source = new MessageSource { Chat = s_group, Sender = phone, SenderAlt = null, RecipientAlt = null, IsFromMe = true },
				MessageIds = [MessageId.From("M1")],
				Timestamp = Deliveries.At,
				Type = ReceiptType.ReadSelf,
				MessageSender = s_member,
			},
			await receipts.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Actions_ShareTheSessionsQueueAndTheIdempotencyOfTheOtherSends()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "message-actions-queue";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var target = new MessageKey { Id = MessageId.From("T1") };
		var reactionId = MessageId.New();
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);

		var text = client.SendTextAsync(s_peer, "first", cancellationToken: ct);
		var held = (await NextAsync(Session, 0, 1, ct))[0];
		var reaction = client.SendReactionAsync(s_peer, target, "👍", reactionId, ct);
		_ = await fixture.Fake.ReleaseSendAsync(new ReleaseSendRequest { SessionId = Session }, cancellationToken: ct);
		_ = await text;
		var reacted = await reaction;

		// The reaction went out behind the held text.
		var after = (await NextAsync(Session, held.Index, 1, ct))[0];
		Assert.Equal(reactionId.Value, after.Message.Id);
		Assert.NotNull(after.Message.Message.ReactionMessage);

		// A repeat of the reaction's id, as a revoke, returns the reaction's result and sends nothing.
		Assert.Equal(reacted, await client.RevokeMessageAsync(s_peer, target, reactionId, ct));
		var next = await client.SendTextAsync(s_peer, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, after.Index, 1, ct))[0].Message.Id);
	}

	private static E2E.Message Revoke(WACommon.MessageKey key) => new() { ProtocolMessage = new E2E.ProtocolMessage { Type = ProtocolType.Revoke, Key = key } };

	private static WACommon.MessageKey Key(Jid chat, bool fromMe, string id, Jid? participant = null)
	{
		var key = new WACommon.MessageKey { RemoteJID = chat.ToString(), FromMe = fromMe, ID = id };
		if (participant is { } p)
			key.Participant = p.ToString();
		return key;
	}

	private static MessageReceived Received(string session, string id, Jid chat, Jid sender, MessageContent content) =>
		MessageChecks.Received(
			SessionId.From(session),
			MessageId.From(id),
			new MessageSource { Chat = chat, Sender = sender, SenderAlt = null, RecipientAlt = null, IsFromMe = false },
			pushName: null,
			Deliveries.At,
			content
		);

	/// <summary>Connects a linked session whose handlers are registered, so its events flow to them.</summary>
	private static async Task ConnectAsync(IWhatsAppClient client, CancellationToken ct)
	{
		await client.ConnectAsync(ct).ConfigureAwait(false);
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
	}

	private async Task DeliverAsync(string session, string id, Jid chat, Jid sender, E2E.Message message, CancellationToken ct, bool isFromMe = false) =>
		_ = await fixture
			.Fake.DeliverMessageAsync(
				new DeliverMessageRequest
				{
					SessionId = session,
					Id = id,
					Chat = chat.ToString(),
					Sender = sender.ToString(),
					IsFromMe = isFromMe,
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
					Message = message,
				},
				cancellationToken: ct
			)
			.ConfigureAwait(false);

	private static async Task<MediaRef> UploadAsync(IWhatsAppClient client, CancellationToken ct)
	{
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1024));
		return await client.UploadMediaAsync(input, MediaType.Image, ct).ConfigureAwait(false);
	}

	/// <summary>The session's next message node after the entry <paramref name="after"/>, past the receipts and acks of what was delivered.</summary>
	private async Task<Sent> NextMessageAsync(string session, ulong after, CancellationToken ct)
	{
		while (true)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			if (next.Message is not null)
				return next;
			after = next.Index;
		}
	}

	/// <summary>The session's next <paramref name="count"/> entries after the entry <paramref name="after"/>.</summary>
	private async Task<IReadOnlyList<Sent>> NextAsync(string session, ulong after, int count, CancellationToken ct)
	{
		var entries = new List<Sent>(count);
		for (var i = 0; i < count; i++)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			entries.Add(next);
			after = next.Index;
		}

		return entries;
	}
}
