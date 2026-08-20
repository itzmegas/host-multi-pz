using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Cloud;

public sealed class DropboxSnapshotStore(HttpClient http, DropboxOAuthClient oauth) : ICloudSnapshotStore
{
    private const int UploadChunkSize = 8 * 1024 * 1024;

    public async Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(archivePath);
        var manifestName = Path.GetFileName(manifestPath);
        await UploadFileAsync(archivePath, archiveName, cancellationToken);
        try { await UploadFileAsync(manifestPath, manifestName, cancellationToken); }
        catch
        {
            try { await DeleteAsync(archiveName, cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, string Id)>();
        string? cursor = null;
        do
        {
            var endpoint = cursor is null ? "files/list_folder" : "files/list_folder/continue";
            var payload = cursor is null ? JsonSerializer.Serialize(new { path = "/snapshots", recursive = false }) : JsonSerializer.Serialize(new { cursor });
            using var response = await ApiAsync(endpoint, payload, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            entries.AddRange(json.RootElement.GetProperty("entries").EnumerateArray()
                .Where(x => x.GetProperty(".tag").GetString() == "file")
                .Select(x => (x.GetProperty("name").GetString()!, x.GetProperty("id").GetString()!)));
            cursor = json.RootElement.GetProperty("has_more").GetBoolean() ? json.RootElement.GetProperty("cursor").GetString() : null;
        } while (cursor is not null);

        var names = entries.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var result = new List<CloudSnapshot>();
        foreach (var manifestEntry in entries.Where(x => x.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var bytes = await DownloadManifestBytesAsync(manifestEntry.Name, cancellationToken);
            try
            {
                var manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(bytes);
                if (manifest is not null && Guid.TryParseExact(manifest.SnapshotId, "N", out _) &&
                    manifestEntry.Name == $"snapshot-{manifest.SnapshotId}.json" && names.Contains(manifest.ArchiveFileName))
                    result.Add(new(manifest.SnapshotId, manifest.CreatedUtc, manifest.ArchiveFileName, manifestEntry.Name));
            }
            catch (JsonException) { }
        }
        return result;
    }

    public async Task DownloadAsync(CloudSnapshot snapshot, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var archivePath = Path.Combine(destinationDirectory, snapshot.ArchiveName);
        var manifestPath = Path.Combine(destinationDirectory, snapshot.ManifestName);
        var archiveTemporary = Path.Combine(destinationDirectory, $".{snapshot.ArchiveName}.{Guid.NewGuid():N}.tmp");
        var manifestTemporary = Path.Combine(destinationDirectory, $".{snapshot.ManifestName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadFileAsync(snapshot.ArchiveName, archiveTemporary, cancellationToken);
            File.Move(archiveTemporary, archivePath);
            try
            {
                await DownloadFileAsync(snapshot.ManifestName, manifestTemporary, cancellationToken);
                File.Move(manifestTemporary, manifestPath);
            }
            catch
            {
                try { File.Delete(archivePath); } catch { }
                throw;
            }
        }
        finally
        {
            File.Delete(archiveTemporary);
            File.Delete(manifestTemporary);
        }
    }

    private async Task UploadFileAsync(string localPath, string name, CancellationToken ct)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            UploadChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > UploadChunkSize)
        {
            await UploadSessionAsync(stream, name, ct);
            return;
        }

        using var request = await ContentRequestAsync("https://content.dropboxapi.com/2/files/upload", ct);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = $"/snapshots/{name}", mode = "add", autorename = false, mute = true }));
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task UploadSessionAsync(Stream stream, string name, CancellationToken ct)
    {
        var buffer = new byte[UploadChunkSize];
        var count = await ReadChunkAsync(stream, buffer, ct);
        using var startResponse = await SendChunkAsync("files/upload_session/start",
            JsonSerializer.Serialize(new { close = false }), buffer, count, ct);
        using var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStreamAsync(ct));
        var sessionId = startJson.RootElement.GetProperty("session_id").GetString()!;
        long offset = count;

        while (offset < stream.Length)
        {
            count = await ReadChunkAsync(stream, buffer, ct);
            var finishing = offset + count == stream.Length;
            var cursor = new { session_id = sessionId, offset };
            var arguments = finishing
                ? JsonSerializer.Serialize(new
                {
                    cursor,
                    commit = new { path = $"/snapshots/{name}", mode = "add", autorename = false, mute = true }
                })
                : JsonSerializer.Serialize(new { cursor, close = false });
            var endpoint = finishing ? "files/upload_session/finish" : "files/upload_session/append_v2";
            using var response = await SendChunkAsync(endpoint, arguments, buffer, count, ct);
            offset += count;
        }
    }

    private async Task<HttpResponseMessage> SendChunkAsync(
        string endpoint, string arguments, byte[] buffer, int count, CancellationToken ct)
    {
        using var request = await ContentRequestAsync($"https://content.dropboxapi.com/2/{endpoint}", ct);
        request.Headers.Add("Dropbox-API-Arg", arguments);
        request.Content = new ByteArrayContent(buffer, 0, count);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await http.SendAsync(request, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct);
            if (read == 0) break;
            count += read;
        }
        return count;
    }

    private async Task<byte[]> DownloadManifestBytesAsync(string name, CancellationToken ct)
    {
        using var request = await ContentRequestAsync("https://content.dropboxapi.com/2/files/download", ct);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = $"/snapshots/{name}" }));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task DownloadFileAsync(string name, string path, CancellationToken ct)
    {
        using var request = await ContentRequestAsync("https://content.dropboxapi.com/2/files/download", ct);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = $"/snapshots/{name}" }));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, ct);
    }

    private async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var response = await ApiAsync("files/delete_v2", JsonSerializer.Serialize(new { path = $"/snapshots/{name}" }), ct);
    }

    private async Task<HttpResponseMessage> ApiAsync(string endpoint, string json, CancellationToken ct)
    {
        using var request = await ContentRequestAsync($"https://api.dropboxapi.com/2/{endpoint}", ct);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<HttpRequestMessage> ContentRequestAsync(string uri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await oauth.GetAccessTokenAsync(ct));
        return request;
    }
}
