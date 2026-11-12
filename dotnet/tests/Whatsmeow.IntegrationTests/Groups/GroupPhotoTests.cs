using Whatsmeow.Contacts;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Groups;

/// <summary>
/// A group's photo through the public client, on the shared fake-mode sidecar (ADR 0070). Each test uses sessions of its
/// own, and its own group, which the admin creates with the member in it. The fake answers a photo by the URL
/// <c>https://pps.whatsapp.net/fake/&lt;id&gt;</c> (fakewhatsapp/groups.go, SetGroupPhoto).
/// </summary>
public sealed class GroupPhotoTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>A photo the fake takes for a JPEG, larger than one chunk, so it travels in several.</summary>
	private static readonly byte[] s_photo = [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Range(0, 150_000).Select(static i => (byte)i)];

	private static Jid Self(string session) => new() { User = session, Server = "s.whatsapp.net" };

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SetFromAStream_TheMemberSeesItAndReadsIt_ThenItsRemoval()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "group-photo-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "group-photo-b", ct);
		using var pictures = new Received<ContactUpdated>(member);
		var group = (await admin.CreateGroupAsync("Team", [Self("group-photo-b")], ct)).Group.Jid;

		var id = await admin.SetGroupPhotoAsync(group, new MemoryStream(s_photo), ct);

		Assert.NotEmpty(id);
		Assert.Equal(ContactChecks.Picture(member.SessionId, group, id), await pictures.NextAsync(ct));
		Assert.Equal(new ProfilePicture.Set { Url = new Uri($"https://pps.whatsapp.net/fake/{id}"), Id = id }, await member.GetProfilePictureAsync(group, ct));

		await admin.RemoveGroupPhotoAsync(group, ct);

		Assert.Equal(ContactChecks.Picture(member.SessionId, group, id: null), await pictures.NextAsync(ct));
		Assert.Equal(new ProfilePicture.None(), await member.GetProfilePictureAsync(group, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task SetGroupPhoto_NotAJpegOrEmpty_ThrowsValidationAndLeavesThePhoto()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "group-photo-refused", ct);
		var group = (await admin.CreateGroupAsync("Team", [], ct)).Group.Jid;
		var id = await admin.SetGroupPhotoAsync(group, new MemoryStream(s_photo), ct);
		var set = new ProfilePicture.Set { Url = new Uri($"https://pps.whatsapp.net/fake/{id}"), Id = id };

		// WhatsApp refuses an image it does not take with 406 [inferred: the fake refuses what is not a JPEG].
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => admin.SetGroupPhotoAsync(group, new MemoryStream([0x89, 0x50, 0x4E, 0x47]), ct));
		Assert.Equal(set, await admin.GetProfilePictureAsync(group, ct));

		// An empty stream is a caller's bug, never a removal.
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => admin.SetGroupPhotoAsync(group, new MemoryStream(), ct));
		Assert.Equal(set, await admin.GetProfilePictureAsync(group, ct));
	}
}
