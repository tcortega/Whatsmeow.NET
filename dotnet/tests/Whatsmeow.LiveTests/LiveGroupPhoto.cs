using Whatsmeow.Contacts;
using Whatsmeow.Core.Errors;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.LiveTests.Control;

namespace Whatsmeow.LiveTests;

/// <summary>The Contacts FullCycle's picture branches on the run's group, for every live test that checks them (ADR 0060).</summary>
internal static class LiveGroupPhoto
{
	/// <summary>
	/// Branches 3 and 4: the live session sets the run's group photo, then removes it; the companion is a member's
	/// device. A set whose answer is unknown is never sent again; the restore removes the photo if the set may have
	/// happened and the removal did not. A failed call is the client's exception, with its reason (<see cref="RpcErrors"/>).
	/// </summary>
	public static async Task SetAndRemoveAsync(LiveSidecar live, LiveGroup group, IWhatsAppClient client, CancellationToken ct)
	{
		var jid = await group.WithPeerAsync(ct).ConfigureAwait(false);
		var control = await live.ControlAsync(ct).ConfigureAwait(false);
		var setPhoto = new SetGroupPhotoRequest
		{
			SessionId = LiveSidecar.LiveSessionId,
			Group = jid.ToString(),
			JpegPath = Path.Combine(AppContext.BaseDirectory, "group-photo.jpg"),
		};
		var removePhoto = new SetGroupPhotoRequest { SessionId = LiveSidecar.LiveSessionId, Group = jid.ToString() };
		var photoSet = false;

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				var set = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: not null }, LiveChecks.Watch, ct);
				// Set before the call: an answer lost on the way may still have set the photo.
				photoSet = true;
				var answer = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(setPhoto, cancellationToken: ct)).ConfigureAwait(false);
				Assert.NotEmpty(answer.PictureId);
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, answer.PictureId), await LiveChecks.WithinAsync(set, "the group photo change").ConfigureAwait(false));

				var removed = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: null }, LiveChecks.Watch, ct);
				_ = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(removePhoto, cancellationToken: ct)).ConfigureAwait(false);
				photoSet = false;
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, id: null), await LiveChecks.WithinAsync(removed, "the group photo removal").ConfigureAwait(false));
			},
			(
				"remove the group photo",
				async t =>
				{
					if (photoSet)
						_ = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(removePhoto, cancellationToken: t)).ConfigureAwait(false);
				}
		)
		).ConfigureAwait(false);
	}
}
