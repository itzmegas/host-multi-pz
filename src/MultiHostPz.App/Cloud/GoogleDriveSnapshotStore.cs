using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Cloud;

public sealed class GoogleDriveSnapshotStore(HttpClient http, GoogleOAuthClient oauth) : ICloudSnapshotStore
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const int UploadChunkSize = 8 * 1024 * 1024;

    public async Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken)
    {
        var folderId = await GetSnapshotsFolderIdAsync(cancellationToken);
        var archiveName = Path.GetFileName(archivePath);
        var manifestName = Path.GetFileName(manifestPath);
        if ((await FindFilesAsync(folderId, [archiveName, manifestName], cancellationToken)).Count != 0)
            throw new IOException("The snapshot identity already exists in Google Drive.");

        var archiveId = await UploadFileAsync(archivePath, archiveName, folderId, "archive", cancellationToken);
        try { await UploadFileAsync(manifestPath, manifestName, folderId, "manifest", cancellationToken); }
        catch
        {
            try { await DeleteAsync(archiveId, cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var folderId = await GetSnapshotsFolderIdAsync(cancellationToken);
        var files = await ListManagedFilesAsync(folderId, cancellationToken);
        EnsureUniqueNames(files);
        var byName = files.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var snapshots = new List<CloudSnapshot>();
        foreach (var manifestFile in files.Where(x => x.Kind == "manifest" && x.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                var bytes = await DownloadBytesAsync(manifestFile.Id, cancellationToken);
                var manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(bytes);
                if (manifest is null || !Guid.TryParseExact(manifest.SnapshotId, "N", out _) ||
                    manifestFile.Name != $"snapshot-{manifest.SnapshotId}.json" ||
                    !byName.TryGetValue(manifest.ArchiveFileName, out var archive) || archive.Kind != "archive") continue;
                snapshots.Add(new(manifest.SnapshotId, manifest.CreatedUtc, manifest.ArchiveFileName,
                    manifestFile.Name, archive.Id, manifestFile.Id));
            }
            catch (JsonException) { }
        }
        return snapshots;
    }

    public async Task DownloadAsync(CloudSnapshot snapshot, string destinationDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ArchiveId) || string.IsNullOrWhiteSpace(snapshot.ManifestId))
            throw new InvalidOperationException("Google Drive snapshot IDs are missing.");
        Directory.CreateDirectory(destinationDirectory);
        var archivePath = Path.Combine(destinationDirectory, snapshot.ArchiveName);
        var manifestPath = Path.Combine(destinationDirectory, snapshot.ManifestName);
        var archiveTemporary = Path.Combine(destinationDirectory, $".{snapshot.ArchiveName}.{Guid.NewGuid():N}.tmp");
        var manifestTemporary = Path.Combine(destinationDirectory, $".{snapshot.ManifestName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadFileAsync(snapshot.ArchiveId, archiveTemporary, cancellationToken);
            File.Move(archiveTemporary, archivePath);
            try
            {
                await DownloadFileAsync(snapshot.ManifestId, manifestTemporary, cancellationToken);
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

    public static string EscapeQueryValue(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private async Task<string> GetSnapshotsFolderIdAsync(CancellationToken ct)
    {
        var root = await FindOrCreateFolderAsync("MultiHostPz", "root", ct);
        return await FindOrCreateFolderAsync("snapshots", root, ct);
    }

    private async Task<string> FindOrCreateFolderAsync(string name, string parentId, CancellationToken ct)
    {
        var query = $"name = '{EscapeQueryValue(name)}' and mimeType = '{FolderMimeType}' and '{EscapeQueryValue(parentId)}' in parents and trashed = false and appProperties has {{ key='multiHostPz' and value='folder' }}";
        var matches = await ListFilesAsync(query, ct);
        if (matches.Count == 0 && parentId == "root")
        {
            var sharedQuery = $"name = '{EscapeQueryValue(name)}' and mimeType = '{FolderMimeType}' and trashed = false and appProperties has {{ key='multiHostPz' and value='folder' }}";
            matches = await ListFilesAsync(sharedQuery, ct);
        }
        if (matches.Count > 1) throw new IOException($"Multiple managed Google Drive folders named '{name}' were found.");
        if (matches.Count == 1) return matches[0].Id;
        return await CreateMetadataAsync(new { name, mimeType = FolderMimeType, parents = new[] { parentId }, appProperties = new { multiHostPz = "folder" } }, ct);
    }

    private async Task<List<DriveFile>> FindFilesAsync(string parentId, IReadOnlyList<string> names, CancellationToken ct)
    {
        var namesQuery = string.Join(" or ", names.Select(x => $"name = '{EscapeQueryValue(x)}'"));
        return await ListFilesAsync($"({namesQuery}) and '{EscapeQueryValue(parentId)}' in parents and trashed = false and appProperties has {{ key='multiHostPz' and value='snapshot' }}", ct);
    }

    private async Task<List<DriveFile>> ListManagedFilesAsync(string parentId, CancellationToken ct) =>
        await ListFilesAsync($"'{EscapeQueryValue(parentId)}' in parents and trashed = false and appProperties has {{ key='multiHostPz' and value='snapshot' }}", ct);

    private async Task<List<DriveFile>> ListFilesAsync(string query, CancellationToken ct)
    {
        var result = new List<DriveFile>();
        string? pageToken = null;
        do
        {
            var url = "https://www.googleapis.com/drive/v3/files?spaces=drive&pageSize=1000&fields=" +
                      Uri.EscapeDataString("nextPageToken,incompleteSearch,files(id,name,mimeType,appProperties)") +
                      "&q=" + Uri.EscapeDataString(query) +
                      (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var response = await SendAsync(HttpMethod.Get, url, null, HttpCompletionOption.ResponseContentRead, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (json.RootElement.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean())
                throw new IOException("Google Drive returned an incomplete search.");
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                var properties = file.TryGetProperty("appProperties", out var p) ? p : default;
                result.Add(new(file.GetProperty("id").GetString()!, file.GetProperty("name").GetString()!,
                    properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("kind", out var kind) ? kind.GetString() : null));
            }
            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var token) ? token.GetString() : null;
        } while (!string.IsNullOrWhiteSpace(pageToken));
        return result;
    }

    private async Task<string> CreateMetadataAsync(object metadata, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id", content,
            HttpCompletionOption.ResponseContentRead, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<string> UploadFileAsync(string path, string name, string parentId, string kind, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        var metadata = JsonSerializer.Serialize(new
        {
            name, parents = new[] { parentId },
            appProperties = new { multiHostPz = "snapshot", kind }
        });
        using var initiation = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id");
        initiation.Headers.Authorization = new("Bearer", await oauth.GetAccessTokenAsync(ct));
        initiation.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        initiation.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        initiation.Content = new StringContent(metadata, Encoding.UTF8, "application/json");
        using var initiated = await http.SendAsync(initiation, ct);
        initiated.EnsureSuccessStatusCode();
        var session = initiated.Headers.Location ?? throw new IOException("Google Drive did not return a resumable upload URI.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, UploadChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[UploadChunkSize];
        long offset = 0;
        while (offset < length)
        {
            var count = await ReadChunkAsync(stream, buffer, ct);
            using var request = new HttpRequestMessage(HttpMethod.Put, session);
            request.Content = new ByteArrayContent(buffer, 0, count);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            using var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode == 308) { offset += count; continue; }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return json.RootElement.GetProperty("id").GetString()!;
        }
        throw new IOException("Google Drive resumable upload ended without a file ID.");
    }

    private async Task<byte[]> DownloadBytesAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media", null,
            HttpCompletionOption.ResponseContentRead, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task DownloadFileAsync(string id, string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media", null,
            HttpCompletionOption.ResponseHeadersRead, ct);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, ct);
        await destination.FlushAsync(ct);
    }

    private async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}", null,
            HttpCompletionOption.ResponseContentRead, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content,
        HttpCompletionOption option, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new("Bearer", await oauth.GetAccessTokenAsync(ct));
        var response = await http.SendAsync(request, option, ct);
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
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

    private static void EnsureUniqueNames(IEnumerable<DriveFile> files)
    {
        if (files.GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new IOException("Duplicate managed snapshot names were found in Google Drive.");
    }

    private sealed record DriveFile(string Id, string Name, string? Kind);
}
