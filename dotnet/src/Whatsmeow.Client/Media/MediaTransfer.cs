using System.Security.Cryptography;
using Grpc.Core;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Transport;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Media;

/// <summary>
/// A session's media calls (media.proto). A path under the media root is handed to the sidecar, which reads or writes
/// it in place; anything else travels in 64 KiB chunks, never as one message (ARCHITECTURE invariant 8): an upload
/// through <see cref="ChunkedCall"/>, a download one chunk at a time, so memory does not grow with the file.
/// </summary>
internal sealed class MediaTransfer(MediaService.MediaServiceClient client, SessionId sessionId, string? mediaRoot, ILogger log)
{
	public async Task<MediaRef> UploadAsync(string path, MediaType type, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		var header = Header(type);
		var full = Path.GetFullPath(path);

		if (UnderRoot(full))
		{
			ThrowIfMissing(full, file: true);
			MediaLog.UploadingByPath(log, sessionId.Value);
			header.Path = full;
			return await UploadAsync(header, content: null, cancellationToken).ConfigureAwait(false);
		}

		var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using (file.ConfigureAwait(false))
		{
			MediaLog.UploadingInChunks(log, sessionId.Value);
			return await UploadAsync(header, file, cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task<MediaRef> UploadAsync(Stream content, MediaType type, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		var header = Header(type);
		MediaLog.UploadingInChunks(log, sessionId.Value);
		return await UploadAsync(header, content, cancellationToken).ConfigureAwait(false);
	}

	public async Task DownloadAsync(MediaRef media, string path, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(media);
		ArgumentException.ThrowIfNullOrEmpty(path);
		var full = Path.GetFullPath(path);

		if (UnderRoot(full))
		{
			ThrowIfMissing(full, file: false);
			MediaLog.DownloadingByPath(log, sessionId.Value);
			await DownloadAsync(media, full, destination: null, cancellationToken).ConfigureAwait(false);
			return;
		}

		// Outside the root this process writes the file: next to the destination, so the move is a rename, and in
		// place only once complete. The sidecar verified it before the first chunk.
		var partial = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.part");
		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

		try
		{
			var file = new FileStream(partial, options);
			await using (file.ConfigureAwait(false))
			{
				MediaLog.DownloadingInChunks(log, sessionId.Value);
				await DownloadAsync(media, path: "", file, cancellationToken).ConfigureAwait(false);
				await file.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			File.Move(partial, full, overwrite: true);
		}
		catch
		{
			Remove(partial);
			throw;
		}
	}

	public Task DownloadAsync(MediaRef media, Stream destination, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(media);
		ArgumentNullException.ThrowIfNull(destination);
		MediaLog.DownloadingInChunks(log, sessionId.Value);
		return DownloadAsync(media, path: "", destination, cancellationToken);
	}

	public async Task RequestRetryAsync(MessageId id, Events.MessageSource source, MediaRef media, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(media);
		_ = await RpcErrors
			.CallAsync(
				client.RequestMediaRetryAsync(
					new RequestMediaRetryRequest
					{
						SessionId = sessionId.Value,
						MessageId = id.Value,
						Chat = source.Chat.ToString(),
						Sender = source.Sender.ToString(),
						IsFromMe = source.IsFromMe,
						Media = MediaMapping.ToProto(media),
					},
					cancellationToken: cancellationToken
				),
				cancellationToken
			)
			.ConfigureAwait(false);
	}

	private UploadMediaRequest.Types.Header Header(MediaType type) =>
		new() { SessionId = sessionId.Value, MediaType = MediaMapping.ToProto(type) };

	/// <summary>
	/// Whether <paramref name="full"/> is inside the media root as written, compared as the sidecar's Go compares it: by
	/// case except on Windows (<c>filepath.Rel</c>), where .NET's own path comparison ignores case on macOS too. The
	/// sidecar resolves the path again and refuses what leaves the root by a symlink, so this only decides who reads or
	/// writes the file.
	/// </summary>
	private bool UnderRoot(string full)
	{
		if (mediaRoot is null)
			return false;
		var root = Path.EndsInDirectorySeparator(mediaRoot) ? mediaRoot : mediaRoot + Path.DirectorySeparatorChar;
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return full.Length > root.Length && full.StartsWith(root, comparison);
	}

	/// <summary>
	/// Throws what this process would throw for <paramref name="full"/> outside the root when it, as a
	/// <paramref name="file"/> to read, or its directory is missing; the sidecar would only call the path invalid. A
	/// file removed after this check still is <see cref="WhatsmeowValidationException"/>.
	/// </summary>
	private static void ThrowIfMissing(string full, bool file)
	{
		if (!Directory.Exists(Path.GetDirectoryName(full)))
			throw new DirectoryNotFoundException($"Could not find a part of the path '{full}'.");
		if (file && !File.Exists(full) && !Directory.Exists(full))
			throw new FileNotFoundException($"Could not find file '{full}'.", full);
	}

	private async Task<MediaRef> UploadAsync(UploadMediaRequest.Types.Header header, Stream? content, CancellationToken cancellationToken)
	{
		var response = await ChunkedCall
			.SendAsync(
				client.UploadMedia(cancellationToken: cancellationToken),
				new UploadMediaRequest { Header = header },
				content,
				static chunk => new UploadMediaRequest { Chunk = chunk },
				cancellationToken
			)
			.ConfigureAwait(false);
		return MediaMapping.ToRef(response.Media);
	}

	/// <summary>Downloads to <paramref name="path"/> by handoff when it is set, else writes the chunks to <paramref name="destination"/>.</summary>
	private async Task DownloadAsync(MediaRef media, string path, Stream? destination, CancellationToken cancellationToken)
	{
		using var call = client.DownloadMedia(
			new DownloadMediaRequest { SessionId = sessionId.Value, Media = MediaMapping.ToProto(media), Path = path },
			cancellationToken: cancellationToken
		);
		while (await NextAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false))
		{
			if (destination is not null)
				await destination.WriteAsync(call.ResponseStream.Current.Chunk.Memory, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Reads the next chunk and maps the call's failure; the destination's own failures stay its own.</summary>
	private static async Task<bool> NextAsync(IAsyncStreamReader<DownloadMediaResponse> stream, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
		catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
		{
			SidecarUnavailableException.ThrowCallCut(e);
			throw;
		}
	}

	private void Remove(string partial)
	{
		try
		{
			File.Delete(partial);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			MediaLog.RemovePartialFailed(log, e, partial);
		}
	}
}
