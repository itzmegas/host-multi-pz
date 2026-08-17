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
        public Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudSnapshot>>([new(snapshot.Manifest!.SnapshotId, snapshot.Manifest.CreatedUtc, Path.GetFileName(snapshot.ArchivePath!), Path.GetFileName(snapshot.ManifestPath!))]);
        public Task DownloadAsync(CloudSnapshot remote, string destinationDirectory, CancellationToken cancellationToken)
        {
            File.Copy(snapshot.ArchivePath!, Path.Combine(destinationDirectory, remote.ArchiveName));
            File.Copy(snapshot.ManifestPath!, Path.Combine(destinationDirectory, remote.ManifestName));
            return Task.CompletedTask;
        }
    }
    private sealed class NotRunning : IProjectZomboidProcessDetector { public bool IsProjectZomboidRunning() => false; }
}
