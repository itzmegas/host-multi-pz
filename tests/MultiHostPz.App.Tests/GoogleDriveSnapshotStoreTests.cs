using System.Net;
using System.Text;
using System.Text.Json;
using MultiHostPz.App.Cloud;
using MultiHostPz.App.Services;
using static MultiHostPz.App.Tests.DropboxOAuthTests;

namespace MultiHostPz.App.Tests;

public sealed class GoogleDriveSnapshotStoreTests
{
    [Fact]
    public void QueryEscaping_EscapesBackslashesBeforeApostrophes() =>
        Assert.Equal("quinn\\'s \\\\paper", GoogleDriveSnapshotStore.EscapeQueryValue("quinn's \\paper"));

    [Fact]
    public async Task Upload_CreatesFoldersAndUsesOrderedBoundedResumableChunks()
    {
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Path, "snapshot-id.zip");
        var manifest = Path.Combine(directory.Path, "snapshot-id.json");
        await File.WriteAllBytesAsync(archive, new byte[(8 * 1024 * 1024) + 7]);
        await File.WriteAllTextAsync(manifest, "manifest");
        var emulator = new DriveEmulator();
        var store = CreateStore(emulator.Handle);

        await store.UploadAsync(archive, manifest, default);

        Assert.Equal(["MultiHostPz", "snapshots"], emulator.CreatedFolders);
        Assert.Equal(["snapshot-id.zip", "snapshot-id.json"], emulator.StartedUploads);
        Assert.Equal([8 * 1024 * 1024, 7, 8], emulator.Chunks.Select(x => x.Size));
        Assert.Equal("bytes 0-8388607/8388615", emulator.Chunks[0].Range);
        Assert.Equal("bytes 8388608-8388614/8388615", emulator.Chunks[1].Range);
    }

    [Fact]
    public async Task ExistingSnapshotIdentity_IsNotOverwritten()
    {
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Path, "snapshot-id.zip");
        var manifest = Path.Combine(directory.Path, "snapshot-id.json");
        await File.WriteAllTextAsync(archive, "zip");
        await File.WriteAllTextAsync(manifest, "json");
        var emulator = new DriveEmulator { ExistingIdentity = true };
        var store = CreateStore(emulator.Handle);

        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync(archive, manifest, default));
        Assert.Empty(emulator.StartedUploads);
    }

    [Fact]
    public async Task ManifestFailure_DeletesUploadedArchive()
    {
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Path, "snapshot-id.zip");
        var manifest = Path.Combine(directory.Path, "snapshot-id.json");
        await File.WriteAllTextAsync(archive, "zip");
        await File.WriteAllTextAsync(manifest, "json");
        var emulator = new DriveEmulator { FailManifestUpload = true };
        var store = CreateStore(emulator.Handle);

        await Assert.ThrowsAsync<HttpRequestException>(() => store.UploadAsync(archive, manifest, default));
        Assert.Equal(["archive-id"], emulator.DeletedIds);
    }

    [Fact]
    public async Task List_HandlesPaginationAndReturnsOnlyValidManagedPairs()
    {
        var id = Guid.NewGuid().ToString("N");
        var manifest = JsonSerializer.Serialize(new LocalSnapshotManifest(1, id, DateTimeOffset.UtcNow, "source", 1, 1, $"snapshot-{id}.zip"));
        var page = 0;
        var store = CreateStore(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("name%20%3D%20%27MultiHostPz%27", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, Files("root-id", "MultiHostPz"));
            if (url.Contains("name%20%3D%20%27snapshots%27", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, Files("snapshots-id", "snapshots"));
            if (url.Contains("pageToken=next", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, $"{{\"files\":[{{\"id\":\"manifest-id\",\"name\":\"snapshot-{id}.json\",\"appProperties\":{{\"multiHostPz\":\"snapshot\",\"kind\":\"manifest\"}}}}]}}");
            if (url.Contains("/files/manifest-id?alt=media", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, manifest);
            if (url.Contains("appProperties", StringComparison.Ordinal) && page++ == 0)
                return Json(HttpStatusCode.OK, $"{{\"nextPageToken\":\"next\",\"files\":[{{\"id\":\"archive-id\",\"name\":\"snapshot-{id}.zip\",\"appProperties\":{{\"multiHostPz\":\"snapshot\",\"kind\":\"archive\"}}}}]}}");
            return Json(HttpStatusCode.OK, "{\"files\":[]}");
        });

        var snapshots = await store.ListAsync(default);
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("archive-id", snapshot.ArchiveId);
        Assert.Equal("manifest-id", snapshot.ManifestId);
    }

    [Fact]
    public async Task Download_StreamsBothFilesToStagedPaths()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamOnlyContent(Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath.Contains("archive-id") ? "archive" : "manifest"))
        });
        var snapshot = new CloudSnapshot("id", DateTimeOffset.UtcNow, "snapshot-id.zip", "snapshot-id.json", "archive-id", "manifest-id");
        await store.DownloadAsync(snapshot, directory.Path, default);
        Assert.Equal("archive", await File.ReadAllTextAsync(Path.Combine(directory.Path, snapshot.ArchiveName)));
        Assert.Equal("manifest", await File.ReadAllTextAsync(Path.Combine(directory.Path, snapshot.ManifestName)));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    private static GoogleDriveSnapshotStore CreateStore(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var tokenDirectory = new TestDirectory();
        var tokens = new GoogleTokenStore(tokenDirectory.Path, new PassthroughProtector());
        tokens.Save(new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var http = new HttpClient(new StubHandler(request => Task.FromResult(handler(request))));
        return new(http, new GoogleOAuthClient(http, tokens, new("id", "secret")));
    }

    private static string Files(string id, string name) => $"{{\"files\":[{{\"id\":\"{id}\",\"name\":\"{name}\",\"appProperties\":{{\"multiHostPz\":\"folder\"}}}}]}}";
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class DriveEmulator
    {
        private int _folderCreates;
        private int _uploadStarts;
        public bool ExistingIdentity { get; init; }
        public bool FailManifestUpload { get; init; }
        public List<string> CreatedFolders { get; } = [];
        public List<string> StartedUploads { get; } = [];
        public List<(int Size, string Range)> Chunks { get; } = [];
        public List<string> DeletedIds { get; } = [];

        public HttpResponseMessage Handle(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Get && url.Contains("name%20%3D%20%27MultiHostPz%27", StringComparison.Ordinal))
                return _folderCreates > 0 ? Json(HttpStatusCode.OK, Files("root-id", "MultiHostPz")) : Json(HttpStatusCode.OK, "{\"files\":[]}");
            if (request.Method == HttpMethod.Get && url.Contains("name%20%3D%20%27snapshots%27", StringComparison.Ordinal))
                return _folderCreates > 1 ? Json(HttpStatusCode.OK, Files("snapshots-id", "snapshots")) : Json(HttpStatusCode.OK, "{\"files\":[]}");
            if (request.Method == HttpMethod.Post && url.Contains("/drive/v3/files?fields=id", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                CreatedFolders.Add(name);
                _folderCreates++;
                return Json(HttpStatusCode.OK, $"{{\"id\":\"{(_folderCreates == 1 ? "root-id" : "snapshots-id")}\"}}");
            }
            if (request.Method == HttpMethod.Get && url.Contains("appProperties", StringComparison.Ordinal))
                return ExistingIdentity ? Json(HttpStatusCode.OK, Files("existing", "snapshot-id.zip")) : Json(HttpStatusCode.OK, "{\"files\":[]}");
            if (request.Method == HttpMethod.Post && url.Contains("uploadType=resumable", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                StartedUploads.Add(name);
                _uploadStarts++;
                var response = Json(HttpStatusCode.OK, "{}");
                response.Headers.Location = new Uri($"https://upload.test/session-{_uploadStarts}");
                return response;
            }
            if (request.Method == HttpMethod.Put && request.RequestUri.Host == "upload.test")
            {
                var size = checked((int)request.Content!.Headers.ContentLength!.Value);
                Chunks.Add((size, request.Content.Headers.ContentRange!.ToString()));
                if (FailManifestUpload && request.RequestUri.AbsolutePath.EndsWith("2", StringComparison.Ordinal)) return Json(HttpStatusCode.InternalServerError, "{}");
                var range = request.Content.Headers.ContentRange!;
                var final = range.To!.Value == range.Length!.Value - 1;
                return final ? Json(HttpStatusCode.OK, $"{{\"id\":\"{(request.RequestUri.AbsolutePath.EndsWith("1", StringComparison.Ordinal) ? "archive-id" : "manifest-id")}\"}}")
                    : new HttpResponseMessage((HttpStatusCode)308);
            }
            if (request.Method == HttpMethod.Delete)
            {
                DeletedIds.Add(request.RequestUri.AbsolutePath.Split('/').Last());
                return Json(HttpStatusCode.NoContent, "{}");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {url}");
        }
    }

    private sealed class StreamOnlyContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Response must stream.");
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value;
        public byte[] Unprotect(byte[] value) => value;
    }
}
