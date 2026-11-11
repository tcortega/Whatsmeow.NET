using Whatsmeow.Contacts;
using Whatsmeow.IntegrationTests.Contacts;

namespace Whatsmeow.LiveTests;

/// <summary>The Contacts FullCycle's picture branches on the run's group, for every live test that checks them (ADR 0060, ADR 0070).</summary>
internal static class LiveGroupPhoto
{
	/// <summary>
	/// Branches 3 and 4: the live session sets the run's group photo through the contract, then removes it; the companion
	/// is a member's device. A set whose answer is unknown is never sent again; the restore removes the photo if the set
	/// may have happened and the removal did not.
	/// </summary>
	public static async Task SetAndRemoveAsync(LiveSidecar live, LiveGroup group, IWhatsAppClient client, CancellationToken ct)
	{
		var jid = await group.WithPeerAsync(ct).ConfigureAwait(false);
		var owner = await live.SessionAsync(LiveSidecar.LiveSessionId, ct).ConfigureAwait(false);
		var photo = Path.Combine(AppContext.BaseDirectory, "group-photo.jpg");
		var photoSet = false;

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				var set = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: not null }, LiveChecks.Watch, ct);
				// Set before the call: an answer lost on the way may still have set the photo.
				photoSet = true;
				var id = await SetFromFileAsync(owner, jid, photo, ct).ConfigureAwait(false);
				Assert.NotEmpty(id);
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, id), await LiveChecks.WithinAsync(set, "the group photo change").ConfigureAwait(false));

				var removed = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: null }, LiveChecks.Watch, ct);
				await owner.RemoveGroupPhotoAsync(jid, ct).ConfigureAwait(false);
				photoSet = false;
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, id: null), await LiveChecks.WithinAsync(removed, "the group photo removal").ConfigureAwait(false));
			},
			(
				"remove the group photo",
				async t =>
				{
					if (photoSet)
						await owner.RemoveGroupPhotoAsync(jid, t).ConfigureAwait(false);
				}
		)
		).ConfigureAwait(false);
	}

	/// <summary>Sets the group's photo from the file at <paramref name="path"/>, read as it is sent.</summary>
	private static async Task<string> SetFromFileAsync(IWhatsAppClient owner, Jid group, string path, CancellationToken ct)
	{
		var file = File.OpenRead(path);
		await using (file.ConfigureAwait(false))
			return await owner.SetGroupPhotoAsync(group, file, ct).ConfigureAwait(false);
	}
}
