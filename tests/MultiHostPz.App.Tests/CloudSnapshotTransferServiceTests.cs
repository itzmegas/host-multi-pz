using MultiHostPz.App.Cloud;
using MultiHostPz.App.Localization;
using MultiHostPz.App.Services;
using static MultiHostPz.App.Tests.DropboxOAuthTests;

namespace MultiHostPz.App.Tests;

public sealed class CloudSnapshotTransferServiceTests
{
    [Fact]
    public async Task Download_StagesValidPairAndPublishesWithoutRestoring()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "remote-save");
        var created = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        var store = new FakeStore(created);
        var service = new CloudSnapshotTransferService(store, local);
        var id = await service.DownloadLatestAsync(default);
        Assert.Equal(created.Manifest!.SnapshotId, id);
        Assert.True(File.Exists(Path.Combine(local, created.Manifest.ArchiveFileName)));
        Assert.True(File.Exists(Path.Combine(local, $"snapshot-{id}.json")));
        Assert.Equal("remote-save", File.ReadAllText(Path.Combine(source, "save.txt")));
    }

    [Fact]
    public async Task Download_DoesNotOverwriteExistingIdentity()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "save");
        var created = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        Directory.CreateDirectory(local); File.WriteAllText(Path.Combine(local, created.Manifest!.ArchiveFileName), "existing");
        var service = new CloudSnapshotTransferService(new FakeStore(created), local);
        await Assert.ThrowsAsync<IOException>(() => service.DownloadLatestAsync(default));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(local, created.Manifest.ArchiveFileName)));
    }

    [Fact]
    public async Task DownloadAndRestore_RestoresDownloadedSnapshotIntoTargetWithBackup()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source"); var target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "remote-save");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "save.txt"), "current-save");
        var created = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        var service = new CloudSnapshotTransferService(new FakeStore(created), local, new NotRunning());

        var result = await service.DownloadAndRestoreLatestAsync(target, default);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal(created.Manifest!.SnapshotId, result.SnapshotId);
        Assert.Equal("remote-save", File.ReadAllText(Path.Combine(target, "save.txt")));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("current-save", File.ReadAllText(Path.Combine(result.BackupPath!, "save.txt")));
    }

    [Fact]
    public async Task DownloadAndRestore_RestoresDownloadedSnapshotNotNewerLocalSnapshot()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source"); var target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "cloud-save");
        var cloudSnapshot = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        File.WriteAllText(Path.Combine(source, "save.txt"), "newer-local-save");
        var newerLocal = new LocalSnapshotService(new NotRunning(), local).CreateSnapshot(source);
        MakeNewer(newerLocal.ManifestPath!, cloudSnapshot.Manifest!.CreatedUtc);
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "save.txt"), "current-save");
        var service = new CloudSnapshotTransferService(new FakeStore(cloudSnapshot), local, new NotRunning());

        var result = await service.DownloadAndRestoreLatestAsync(target, default);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal(cloudSnapshot.Manifest!.SnapshotId, result.SnapshotId);
        Assert.Equal("cloud-save", File.ReadAllText(Path.Combine(target, "save.txt")));
    }

    [Fact]
    public async Task DownloadAndRestore_FailsBeforeDownloadWhenProjectZomboidRunning()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source"); var target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "remote-save");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "save.txt"), "current-save");
        var created = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        var store = new FakeStore(created);
        var service = new CloudSnapshotTransferService(store, local, new Running());

        var result = await service.DownloadAndRestoreLatestAsync(target, default);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.ProjectZomboidRunning, result.FailureReason);
        Assert.Equal(0, store.Downloads);
        Assert.Equal("current-save", File.ReadAllText(Path.Combine(target, "save.txt")));
    }

    [Fact]
    public async Task DownloadAndRestore_FailsWhenTargetDirectoryMissing()
    {
        using var root = new TestDirectory();
        var remote = Path.Combine(root.Path, "remote"); var local = Path.Combine(root.Path, "local"); var source = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "save.txt"), "remote-save");
        var created = new LocalSnapshotService(new NotRunning(), remote).CreateSnapshot(source);
        var store = new FakeStore(created);
        var service = new CloudSnapshotTransferService(store, local, new NotRunning());

        var result = await service.DownloadAndRestoreLatestAsync(Path.Combine(root.Path, "missing"), default);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.TargetMissing, result.FailureReason);
        Assert.Equal(0, store.Downloads);
    }

    [Fact]
    public void DownloadRestoredStatus_IsLocalized()
    {
        Assert.Equal("Downloaded and restored snapshot id-1 from Google Drive.",
            new LocalizedText("en").Format(new(CloudStatusKind.DownloadRestored, "id-1"), CloudProviderKind.GoogleDrive));
        Assert.Equal("Se descargó y restauró la instantánea id-1 de Google Drive.",
            new LocalizedText("es").Format(new(CloudStatusKind.DownloadRestored, "id-1"), CloudProviderKind.GoogleDrive));
    }

    [Fact]
    public void CloudStatuses_AreLocalized()
    {
        Assert.Equal("Disconnected", new LocalizedText("en").Format(new(CloudStatusKind.Disconnected)));
        Assert.Equal("Desconectado", new LocalizedText("es").Format(new(CloudStatusKind.Disconnected)));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, false)]
    public void CloudActions_RequireConfigurationAndConnection(
        bool configured, bool connected, bool busy, bool canConnect, bool canUseConnectedAction)
    {
        Assert.Equal(canConnect, CloudActionAvailability.CanConnect(configured, connected, busy));
        Assert.Equal(canUseConnectedAction,
            CloudActionAvailability.CanUseConnectedAction(configured, connected, busy));
    }

    private sealed class FakeStore(LocalSnapshotResult snapshot) : ICloudSnapshotStore
    {
        public int Downloads;

        public Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudSnapshot>>([new(snapshot.Manifest!.SnapshotId, snapshot.Manifest.CreatedUtc, Path.GetFileName(snapshot.ArchivePath!), Path.GetFileName(snapshot.ManifestPath!))]);
        public Task DownloadAsync(CloudSnapshot remote, string destinationDirectory, CancellationToken cancellationToken)
        {
            Downloads++;
            File.Copy(snapshot.ArchivePath!, Path.Combine(destinationDirectory, remote.ArchiveName));
            File.Copy(snapshot.ManifestPath!, Path.Combine(destinationDirectory, remote.ManifestName));
            return Task.CompletedTask;
        }
    }
    private sealed class NotRunning : IProjectZomboidProcessDetector { public bool IsProjectZomboidRunning() => false; }
    private sealed class Running : IProjectZomboidProcessDetector { public bool IsProjectZomboidRunning() => true; }

    private static void MakeNewer(string manifestPath, DateTimeOffset olderThan)
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<LocalSnapshotManifest>(File.ReadAllText(manifestPath))!;
        var newer = manifest with { CreatedUtc = olderThan.AddHours(1) };
        File.WriteAllText(manifestPath, System.Text.Json.JsonSerializer.Serialize(newer));
    }
}
