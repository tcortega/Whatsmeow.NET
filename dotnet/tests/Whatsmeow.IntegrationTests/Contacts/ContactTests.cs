using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Contacts;

/// <summary>
/// The Contacts &amp; LID slice through the public client, on the shared fake-mode sidecar. The fake WhatsApp's user
/// directory is shared by every test, so each test uses numbers of its own: 55119N... and N...@lid, N its own digit.
/// </summary>
public sealed class ContactTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private static Jid Pn(string user) => new() { User = user, Server = "s.whatsapp.net" };

	private static Jid Lid(string user) => new() { User = user, Server = "lid" };

	[Fact(Timeout = Timeout)]
	public async Task IsOnWhatsApp_NumbersOnAndOff_EachSaysSoWithItsJidAndLid()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-check", ct);
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = "5511910000001@s.whatsapp.net", Lid = "910000001@lid" }, cancellationToken: ct);

		Assert.Equal(
			[
				new PhoneNumberCheck { PhoneNumber = "+5511910000001", IsOnWhatsApp = true, Jid = Pn("5511910000001"), Lid = Lid("910000001") },
				new PhoneNumberCheck { PhoneNumber = "5511910000002", IsOnWhatsApp = false, Jid = null, Lid = null },
			],
			await client.IsOnWhatsAppAsync(["+5511910000001", "5511910000002"], ct)
		);

		// The check taught the store the pair.
		Assert.Equal(
			[new ResolvedJid { Jid = Pn("5511910000001"), Alt = Lid("910000001") }],
			await client.ResolveJidsAsync([Pn("5511910000001")], ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task ResolveJids_AfterAMessageWithBothForms_EachGetsTheOtherAndAnUnknownLidIsUnknown()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "contacts-resolve", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		_ = await fixture.Fake.DeliverMessageAsync(
			new DeliverMessageRequest
			{
				SessionId = "contacts-resolve",
				Id = "M1",
				Chat = "920000001@lid",
				Sender = "920000001:2@lid",
				SenderAlt = "5511920000001:2@s.whatsapp.net",
				Text = "hi",
			},
			cancellationToken: ct
		);
		var message = await received.NextAsync(ct);

		// Sender and SenderAlt together give both forms, and each says which it is.
		Assert.Equal((true, false), (message.Source.Sender.IsLid, message.Source.Sender.IsPhoneNumber));
		Assert.Equal((false, true), (message.Source.SenderAlt!.Value.IsLid, message.Source.SenderAlt.Value.IsPhoneNumber));

		// The store answers, so a disconnected session resolves too.
		await client.DisconnectAsync(ct);
		var resolved = await client.ResolveJidsAsync([Lid("920000001"), Pn("5511920000001"), Lid("920000999")], ct);

		Assert.Equal(
			[
				new ResolvedJid { Jid = Lid("920000001"), Alt = Pn("5511920000001") },
				new ResolvedJid { Jid = Pn("5511920000001"), Alt = Lid("920000001") },
				new ResolvedJid { Jid = Lid("920000999"), Alt = null },
			],
			resolved
		);
		Assert.Equal([true, false, true], resolved.Select(r => r.Jid.IsLid));
		Assert.Equal([false, true, false], resolved.Select(r => r.Jid.IsPhoneNumber));

		// A group is neither form.
		var group = Jid.Parse("120363920000000000@g.us");
		Assert.Equal((false, false), (group.IsLid, group.IsPhoneNumber));
	}

	[Fact(Timeout = Timeout)]
	public async Task GetUserInfo_Users_TheApplicationGetsAboutBusinessNameAndPictureId()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-userinfo", ct);
		_ = await fixture.Fake.RegisterUserAsync(
			new RegisterUserRequest { PhoneJid = "5511930000001@s.whatsapp.net", Lid = "930000001@lid", About = "hi", BusinessName = "Ana's Shop" },
			cancellationToken: ct
		);
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = "5511930000002@s.whatsapp.net", About = "busy" }, cancellationToken: ct);
		_ = await fixture.Fake.SetProfilePictureAsync(
			new SetProfilePictureRequest { Jid = "5511930000001@s.whatsapp.net", Set = new SetProfilePictureRequest.Types.Picture { Id = "P1", Url = "https://pps.example/p1" } },
			cancellationToken: ct
		);

		Assert.Equal(
			[
				new UserInfo { Jid = Pn("5511930000001"), About = "hi", VerifiedBusinessName = "Ana's Shop", PictureId = "P1" },
				new UserInfo { Jid = Pn("5511930000002"), About = "busy", VerifiedBusinessName = null, PictureId = null },
			],
			await client.GetUserInfoAsync([Pn("5511930000002"), Pn("5511930000001"), Pn("5511930000009")], ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GetProfilePicture_SetNoneHiddenAndAGroup_EachSaysSo()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-picture", ct);
		_ = await fixture.Fake.SetProfilePictureAsync(
			new SetProfilePictureRequest { Jid = "5511940000001@s.whatsapp.net", Set = new SetProfilePictureRequest.Types.Picture { Id = "P1", Url = "https://pps.example/p1" } },
			cancellationToken: ct
		);
		_ = await fixture.Fake.SetProfilePictureAsync(
			new SetProfilePictureRequest { Jid = "120363940000000000@g.us", Set = new SetProfilePictureRequest.Types.Picture { Id = "G1", Url = "https://pps.example/g1" } },
			cancellationToken: ct
		);
		_ = await fixture.Fake.SetProfilePictureAsync(
			new SetProfilePictureRequest { Jid = "940000002@lid", Hidden = new SetProfilePictureRequest.Types.Hidden() },
			cancellationToken: ct
		);

		Assert.Equal(
			new ProfilePicture.Set { Url = new Uri("https://pps.example/p1"), Id = "P1" },
			await client.GetProfilePictureAsync(Pn("5511940000001"), ct)
		);
		Assert.Equal(
			new ProfilePicture.Set { Url = new Uri("https://pps.example/g1"), Id = "G1" },
			await client.GetProfilePictureAsync(new Jid { User = "120363940000000000", Server = "g.us" }, ct)
		);
		Assert.Equal(new ProfilePicture.Hidden(), await client.GetProfilePictureAsync(Lid("940000002"), ct));
		Assert.Equal(new ProfilePicture.None(), await client.GetProfilePictureAsync(Lid("940000003"), ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_ContactChanges_TheHandlerGetsThemAndTheContactsShowThem()
	{
		var ct = TestContext.Current.CancellationToken;
		var session = SessionId.From("contacts-fullcycle");
		var client = await SessionSetup.LinkedAsync(fixture, "contacts-fullcycle", ct);
		using var received = new Received<ContactUpdated>(client);
		await client.ConnectAsync(ct);

		// A message carries a new push name (message.go:59-61).
		_ = await fixture.Fake.DeliverMessageAsync(
			new DeliverMessageRequest
			{
				SessionId = "contacts-fullcycle",
				Id = "M1",
				Chat = "950000001@lid",
				Sender = "950000001@lid",
				SenderAlt = "5511950000001@s.whatsapp.net",
				PushName = "Ana",
				Text = "hi",
			},
			cancellationToken: ct
		);
		var ana = new ContactUpdated
		{
			SessionId = session,
			Jid = Lid("950000001"),
			JidAlt = Pn("5511950000001"),
			Change = new ContactChange.PushName { Name = "Ana" },
		};
		Assert.Equal(ana, await received.NextAsync(ct));

		// Reading a business's profile stores its verified name (user.go:269-271).
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = "5511950000002@s.whatsapp.net", BusinessName = "Bea's Shop" }, cancellationToken: ct);
		_ = await client.GetUserInfoAsync([Pn("5511950000002")], ct);
		Assert.Equal(
			ana with { Jid = Pn("5511950000002"), JidAlt = null, Change = new ContactChange.BusinessName { Name = "Bea's Shop" } },
			await received.NextAsync(ct)
		);

		// Picture and status notifications.
		_ = await fixture.Fake.DeliverContactNotificationAsync(
			new DeliverContactNotificationRequest
			{
				SessionId = "contacts-fullcycle",
				Picture = new DeliverContactNotificationRequest.Types.Picture { Jid = "950000001@lid", Id = "P2" },
			},
			cancellationToken: ct
		);
		Assert.Equal(ana with { JidAlt = null, Change = new ContactChange.Picture { Id = "P2" } }, await received.NextAsync(ct));

		_ = await fixture.Fake.DeliverContactNotificationAsync(
			new DeliverContactNotificationRequest
			{
				SessionId = "contacts-fullcycle",
				Picture = new DeliverContactNotificationRequest.Types.Picture { Jid = "950000001@lid", Removed = true },
			},
			cancellationToken: ct
		);
		Assert.Equal(ana with { JidAlt = null, Change = new ContactChange.Picture { Id = null } }, await received.NextAsync(ct));

		_ = await fixture.Fake.DeliverContactNotificationAsync(
			new DeliverContactNotificationRequest
			{
				SessionId = "contacts-fullcycle",
				About = new DeliverContactNotificationRequest.Types.About { Jid = "950000001@lid", Text = "busy" },
			},
			cancellationToken: ct
		);
		Assert.Equal(ana with { JidAlt = null, Change = new ContactChange.About { Text = "busy" } }, await received.NextAsync(ct));

		// An app state sync brings the saved name.
		_ = await fixture.Fake.SyncContactAsync(
			new SyncContactRequest { SessionId = "contacts-fullcycle", Jid = "5511950000002@s.whatsapp.net", FirstName = "Bea", FullName = "Bea Lima" },
			cancellationToken: ct
		);

		Assert.Equal(
			[
				new Contact { Jid = Pn("5511950000001"), SavedName = null, PushName = "Ana", BusinessName = null },
				new Contact { Jid = Pn("5511950000002"), SavedName = "Bea Lima", PushName = null, BusinessName = "Bea's Shop" },
				new Contact { Jid = Lid("950000001"), SavedName = null, PushName = "Ana", BusinessName = null },
			],
			await client.GetContactsAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Blocklist_BlockThenUnblock_TheNextReadReflectsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-block", ct);
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = "5511960000001@s.whatsapp.net", Lid = "960000001@lid" }, cancellationToken: ct);
		Assert.Empty(await client.GetBlocklistAsync(ct));

		await client.BlockAsync(Pn("5511960000001"), ct);
		Assert.Equal([new BlockedUser { Jid = Lid("960000001"), JidAlt = Pn("5511960000001") }], await client.GetBlocklistAsync(ct));

		await client.UnblockAsync(Lid("960000001"), ct);
		Assert.Empty(await client.GetBlocklistAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Block_ANumberNotOnWhatsApp_ThrowsRecipientNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-block-nobody", ct);

		var thrown = await Assert.ThrowsAsync<RecipientNotFoundException>(() => client.BlockAsync(Pn("5511970000009"), ct));
		Assert.Equal(WhatsmeowErrorCode.RecipientNotOnWhatsApp, thrown.ErrorCode);
		Assert.Empty(await client.GetBlocklistAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Lookups_EmptyListsReturnEmptyAndAMalformedNumberThrowsValidation()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-inputs", ct);

		Assert.Empty(await client.IsOnWhatsAppAsync([], ct));
		Assert.Empty(await client.GetUserInfoAsync([], ct));
		Assert.Empty(await client.ResolveJidsAsync([], ct));

		var thrown = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.IsOnWhatsAppAsync(["+5511980000001", "+55 11 98"], ct));
		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, thrown.ErrorCode);
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.BlockAsync(new Jid { User = "120363980000000000", Server = "g.us" }, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task IsOnWhatsApp_RateLimited_ThrowsRateLimited()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "contacts-ratelimit", ct);
		_ = await fixture.Fake.FailNextContactQueryAsync(new FailNextContactQueryRequest { SessionId = "contacts-ratelimit", Code = 429 }, cancellationToken: ct);

		var thrown = await Assert.ThrowsAsync<RateLimitedException>(() => client.IsOnWhatsAppAsync(["+5511990000001"], ct));
		Assert.Equal(WhatsmeowErrorCode.RateLimited, thrown.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Lookups_SessionNotOnline_FailAtOnceAndStoreReadsNeedAPairedSession()
	{
		var ct = TestContext.Current.CancellationToken;
		var linked = await SessionSetup.LinkedAsync(fixture, "contacts-offline", ct);
		var unpaired = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("contacts-unpaired"), ct);

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => linked.IsOnWhatsAppAsync(["+5511900000001"], ct));
		_ = await Assert.ThrowsAsync<NotConnectedException>(() => linked.GetBlocklistAsync(ct));
		Assert.Empty(await linked.GetContactsAsync(ct));

		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.IsOnWhatsAppAsync(["+5511900000001"], ct));
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.GetContactsAsync(ct));
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.ResolveJidsAsync([Lid("900000001")], ct));
	}
}
