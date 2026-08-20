using System.Net;
using System.Text;
using System.Text.Json;
using MultiHostPz.App.Cloud;
using MultiHostPz.App.Services;
using static MultiHostPz.App.Tests.DropboxOAuthTests;

namespace MultiHostPz.App.Tests;

public sealed class DropboxSnapshotStoreTests
{
    [Fact]
    public async Task Upload_PublishesArchiveThenManifest()
    {
        using var directory = new TestDirectory();
        var snapshot = CreateSnapshot(directory.Path);
        var uploaded = new List<string>();
        var store = CreateStore(request =>
        {
            uploaded.Add(request.Headers.GetValues("Dropbox-API-Arg").Single());
            return Json(HttpStatusCode.OK, "{}");
        });
        await store.UploadAsync(snapshot.ArchivePath!, snapshot.ManifestPath!, default);
        Assert.Contains(".zip", uploaded[0]);
        Assert.Contains(".json", uploaded[1]);
        Assert.All(uploaded, value => Assert.Contains("\"mode\":\"add\"", value));
    }

    [Fact]
    public async Task LargeUpload_UsesSequentialSessionChunksAndAddCommit()
    {
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Path, "snapshot-large.zip");
        var manifest = Path.Combine(directory.Path, "snapshot-large.json");
        await File.WriteAllBytesAsync(archive, new byte[(16 * 1024 * 1024) + 17]);
        await File.WriteAllTextAsync(manifest, "{}");
        var calls = new List<(string Path, string Arguments, int Size)>();
        var store = CreateStore(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var arguments = request.Headers.GetValues("Dropbox-API-Arg").Single();
            var size = checked((int)(request.Content!.Headers.ContentLength ?? -1));
            calls.Add((path, arguments, size));
            return path.EndsWith("/start", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"session_id\":\"session-1\"}")
                : Json(HttpStatusCode.OK, "{}");
        });

        await store.UploadAsync(archive, manifest, default);

        Assert.Equal([
            "/2/files/upload_session/start",
            "/2/files/upload_session/append_v2",
            "/2/files/upload_session/finish",
            "/2/files/upload"
        ], calls.Select(x => x.Path));
        Assert.Equal([8 * 1024 * 1024, 8 * 1024 * 1024, 17], calls.Take(3).Select(x => x.Size));
        Assert.False(JsonDocument.Parse(calls[0].Arguments).RootElement.GetProperty("close").GetBoolean());
        Assert.Equal(8 * 1024 * 1024, JsonDocument.Parse(calls[1].Arguments).RootElement.GetProperty("cursor").GetProperty("offset").GetInt64());
        using var finish = JsonDocument.Parse(calls[2].Arguments);
        Assert.Equal(16 * 1024 * 1024, finish.RootElement.GetProperty("cursor").GetProperty("offset").GetInt64());
        var commit = finish.RootElement.GetProperty("commit");
        Assert.Equal("add", commit.GetProperty("mode").GetString());
        Assert.False(commit.GetProperty("autorename").GetBoolean());
        Assert.True(commit.GetProperty("mute").GetBoolean());
    }

    [Fact]
    public async Task Download_StreamsToTemporaryFilesAndPublishesArchiveThenManifest()
    {
        using var directory = new TestDirectory();
        var publishedDuringManifestDownload = false;
        var snapshot = new CloudSnapshot("id", DateTimeOffset.UtcNow, "snapshot-id.zip", "snapshot-id.json");
        var store = CreateStore(request =>
        {
            var arguments = request.Headers.GetValues("Dropbox-API-Arg").Single();
            if (arguments.Contains(".json", StringComparison.Ordinal))
                publishedDuringManifestDownload = File.Exists(Path.Combine(directory.Path, snapshot.ArchiveName));
            var bytes = Encoding.UTF8.GetBytes(arguments.Contains(".zip", StringComparison.Ordinal) ? "archive" : "manifest");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamOnlyContent(bytes) };
        });

        await store.DownloadAsync(snapshot, directory.Path, default);

        Assert.True(publishedDuringManifestDownload);
        Assert.Equal("archive", await File.ReadAllTextAsync(Path.Combine(directory.Path, snapshot.ArchiveName)));
        Assert.Equal("manifest", await File.ReadAllTextAsync(Path.Combine(directory.Path, snapshot.ManifestName)));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ManifestDownloadFailure_RemovesArchiveAndTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var snapshot = new CloudSnapshot("id", DateTimeOffset.UtcNow, "snapshot-id.zip", "snapshot-id.json");
        var store = CreateStore(request => request.Headers.GetValues("Dropbox-API-Arg").Single().Contains(".json", StringComparison.Ordinal)
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamOnlyContent(Encoding.UTF8.GetBytes("archive")) });

        await Assert.ThrowsAsync<HttpRequestException>(() => store.DownloadAsync(snapshot, directory.Path, default));

        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task ManifestUploadFailure_DeletesPublishedArchive()
    {
        using var directory = new TestDirectory();
        var snapshot = CreateSnapshot(directory.Path);
        var calls = new List<string>();
        var store = CreateStore(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return calls.Count == 2 ? Json(HttpStatusCode.Conflict, "{}") : Json(HttpStatusCode.OK, "{}");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => store.UploadAsync(snapshot.ArchivePath!, snapshot.ManifestPath!, default));
        Assert.Equal("/2/files/delete_v2", calls[2]);
    }

    [Fact]
    public async Task List_ReturnsOnlyManifestArchivePairs()
    {
        var id = Guid.NewGuid().ToString("N");
        var manifest = JsonSerializer.Serialize(new LocalSnapshotManifest(1, id, DateTimeOffset.UtcNow, "source", 1, 1, $"snapshot-{id}.zip"));
        var store = CreateStore(request => request.RequestUri!.AbsolutePath switch
        {
            "/2/files/list_folder" => Json(HttpStatusCode.OK, $"{{\"entries\":[{{\".tag\":\"file\",\"name\":\"snapshot-{id}.zip\",\"id\":\"1\"}},{{\".tag\":\"file\",\"name\":\"snapshot-{id}.json\",\"id\":\"2\"}},{{\".tag\":\"file\",\"name\":\"snapshot-orphan.json\",\"id\":\"3\"}}],\"has_more\":false,\"cursor\":\"c\"}}"),
            "/2/files/download" when request.Headers.GetValues("Dropbox-API-Arg").Single().Contains(id) => Json(HttpStatusCode.OK, manifest),
            _ => Json(HttpStatusCode.OK, "not-json")
        });
        var snapshots = await store.ListAsync(default);
        Assert.Single(snapshots);
        Assert.Equal(id, snapshots[0].SnapshotId);
    }

    private static DropboxSnapshotStore CreateStore(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var tokens = new DropboxTokenStore(new TestDirectory().Path, new PassthroughProtector());
        tokens.Save(new("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        var http = new HttpClient(new StubHandler(request => Task.FromResult(handler(request))));
        return new(http, new DropboxOAuthClient(http, tokens, "key"));
    }

    private static LocalSnapshotResult CreateSnapshot(string root)
    {
        var source = Path.Combine(root, "source"); var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "save");
        return new LocalSnapshotService(new NotRunning(), snapshots).CreateSnapshot(source);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class StreamOnlyContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The response body must not be buffered.");
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
    private sealed class PassthroughProtector : ISecretProtector { public byte[] Protect(byte[] value) => value; public byte[] Unprotect(byte[] value) => value; }
    private sealed class NotRunning : IProjectZomboidProcessDetector { public bool IsProjectZomboidRunning() => false; }
}
