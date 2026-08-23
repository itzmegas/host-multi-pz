using System.IO.Compression;
using System.Text.Json;
using MultiHostPz.App.Services;
using static MultiHostPz.App.Tests.DropboxOAuthTests;

namespace MultiHostPz.App.Tests;

public sealed class LocalSnapshotRestoreServiceTests
{
    [Fact]
    public void RestoreSnapshot_ReplacesTargetAndPreservesBackup()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateSourceFile("server.ini", "restored-server");
        var snapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current-server");

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            snapshot.ArchivePath,
            snapshot.ManifestPath);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal(snapshot.Manifest!.SnapshotId, result.SnapshotId);
        Assert.NotNull(result.BackupPath);
        Assert.True(Directory.Exists(result.BackupPath));
        Assert.Equal("restored-server", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
        Assert.Equal("current-server", File.ReadAllText(
            Path.Combine(result.BackupPath!, "server.ini")));
    }

    [Fact]
    public void RestoreSnapshot_WithServerRestoresBothDirectoriesAndPreservesBackup()
    {
        using var root = new TestDirectory();
        var sourceProfile = Path.Combine(root.Path, "source-profile");
        var sourceSaves = Path.Combine(sourceProfile, "Saves", "Multiplayer");
        var sourceServer = Path.Combine(sourceProfile, "Server");
        var targetProfile = Path.Combine(root.Path, "target-profile");
        var targetSaves = Path.Combine(targetProfile, "Saves", "Multiplayer");
        var targetServer = Path.Combine(targetProfile, "Server");
        var snapshots = Path.Combine(root.Path, "snapshots");
        Directory.CreateDirectory(sourceSaves);
        Directory.CreateDirectory(sourceServer);
        Directory.CreateDirectory(targetSaves);
        Directory.CreateDirectory(targetServer);
        Directory.CreateDirectory(snapshots);
        File.WriteAllText(Path.Combine(sourceSaves, "save.bin"), "restored-save");
        File.WriteAllText(Path.Combine(sourceServer, "server.ini"), "restored-server");
        File.WriteAllText(Path.Combine(targetSaves, "save.bin"), "current-save");
        File.WriteAllText(Path.Combine(targetServer, "server.ini"), "current-server");

        var snapshot = new LocalSnapshotService(new TestProcessDetector(false), snapshots)
            .CreateSnapshot(sourceSaves, sourceServer);
        Assert.True(snapshot.Succeeded, snapshot.ErrorMessage);

        var result = new LocalSnapshotRestoreService(new TestProcessDetector(false), snapshots)
            .RestoreSnapshot(targetSaves, snapshot.ArchivePath, snapshot.ManifestPath, targetServer);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal("restored-save", File.ReadAllText(Path.Combine(targetSaves, "save.bin")));
        Assert.Equal("restored-server", File.ReadAllText(Path.Combine(targetServer, "server.ini")));
        Assert.Equal("current-save", File.ReadAllText(Path.Combine(result.BackupPath!, "Saves", "Multiplayer", "save.bin")));
        Assert.Equal("current-server", File.ReadAllText(Path.Combine(result.BackupPath!, "Server", "server.ini")));
    }

    [Fact]
    public void RestoreLatestSnapshot_UsesNewestValidSnapshot()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateSourceFile("server.ini", "first");
        fixture.CreateSnapshot();
        fixture.CreateSourceFile("server.ini", "latest");
        var latestSnapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current");

        var result = fixture.RestoreService.RestoreLatestSnapshot(fixture.TargetDirectory);

        Assert.True(result.Succeeded, result.SafeMessage);
        Assert.Equal(latestSnapshot.Manifest!.SnapshotId, result.SnapshotId);
        Assert.Equal("latest", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
    }

    [Fact]
    public void RestoreSnapshot_RejectsRunningProjectZomboid()
    {
        using var fixture = new RestoreFixture(projectZomboidRunning: true);
        fixture.CreateSourceFile("server.ini", "restored");
        var snapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current");

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            snapshot.ArchivePath,
            snapshot.ManifestPath);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.ProjectZomboidRunning, result.FailureReason);
        Assert.Equal("current", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public void RestoreSnapshot_RejectsMalformedManifest()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateSourceFile("server.ini", "restored");
        var snapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current");
        File.WriteAllText(snapshot.ManifestPath!, "not-json");

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            snapshot.ArchivePath,
            snapshot.ManifestPath);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.ManifestMalformed, result.FailureReason);
        Assert.Equal("current", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
    }

    [Fact]
    public void RestoreSnapshot_RejectsMismatchedManifestIdentity()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateSourceFile("server.ini", "restored");
        var snapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current");
        var manifest = snapshot.Manifest! with
        {
            ArchiveFileName = "snapshot-not-the-archive.zip"
        };
        File.WriteAllText(snapshot.ManifestPath!, JsonSerializer.Serialize(manifest));

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            snapshot.ArchivePath,
            snapshot.ManifestPath);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.ArchiveManifestIdentityMismatch, result.FailureReason);
        Assert.Equal("current", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
    }

    [Fact]
    public void RestoreSnapshot_RejectsPathTraversalBeforeChangingTarget()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateTargetFile("server.ini", "current");
        var snapshotId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(
            fixture.SnapshotsDirectory,
            $"snapshot-{snapshotId}.zip");
        var manifestPath = Path.Combine(
            fixture.SnapshotsDirectory,
            $"snapshot-{snapshotId}.json");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escaped.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        var manifest = new LocalSnapshotManifest(
            1,
            snapshotId,
            DateTimeOffset.UtcNow,
            "source",
            1,
            "unsafe".Length,
            Path.GetFileName(archivePath));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            archivePath,
            manifestPath);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.InvalidZipEntry, result.FailureReason);
        Assert.Equal("current", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
        Assert.False(File.Exists(Path.Combine(fixture.RootDirectory, "escaped.txt")));
    }

    [Fact]
    public void RestoreSnapshot_RejectsValidationMismatchWithoutChangingTarget()
    {
        using var fixture = new RestoreFixture();
        fixture.CreateSourceFile("server.ini", "restored");
        var snapshot = fixture.CreateSnapshot();
        fixture.CreateTargetFile("server.ini", "current");
        var manifest = snapshot.Manifest! with { FileCount = 99 };
        File.WriteAllText(snapshot.ManifestPath!, JsonSerializer.Serialize(manifest));

        var result = fixture.RestoreService.RestoreSnapshot(
            fixture.TargetDirectory,
            snapshot.ArchivePath,
            snapshot.ManifestPath);

        Assert.False(result.Succeeded);
        Assert.Equal(RestoreFailureReason.FileCountMismatch, result.FailureReason);
        Assert.Null(result.BackupPath);
        Assert.Equal("current", File.ReadAllText(
            Path.Combine(fixture.TargetDirectory, "server.ini")));
    }

    private sealed class RestoreFixture : IDisposable
    {
        public RestoreFixture(bool projectZomboidRunning = false)
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"MultiHostPzRestoreTests-{Guid.NewGuid():N}");
            SourceDirectory = Path.Combine(RootDirectory, "source");
            TargetDirectory = Path.Combine(RootDirectory, "target");
            SnapshotsDirectory = Path.Combine(RootDirectory, "snapshots");
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(TargetDirectory);
            Directory.CreateDirectory(SnapshotsDirectory);
            SnapshotService = new LocalSnapshotService(
                new TestProcessDetector(false),
                SnapshotsDirectory);
            RestoreService = new LocalSnapshotRestoreService(
                new TestProcessDetector(projectZomboidRunning),
                SnapshotsDirectory);
        }

        public string RootDirectory { get; }

        public string SourceDirectory { get; }

        public string TargetDirectory { get; }

        public string SnapshotsDirectory { get; }

        public LocalSnapshotService SnapshotService { get; }

        public LocalSnapshotRestoreService RestoreService { get; }

        public void CreateSourceFile(string relativePath, string content)
        {
            WriteFile(SourceDirectory, relativePath, content);
        }

        public void CreateTargetFile(string relativePath, string content)
        {
            WriteFile(TargetDirectory, relativePath, content);
        }

        public LocalSnapshotResult CreateSnapshot()
        {
            var result = SnapshotService.CreateSnapshot(SourceDirectory);
            Assert.True(result.Succeeded, result.ErrorMessage);
            return result;
        }

        public void Dispose()
        {
            Directory.Delete(RootDirectory, recursive: true);
        }

        private static void WriteFile(string rootDirectory, string relativePath, string content)
        {
            var fullPath = Path.Combine(rootDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
    }

    private sealed class TestProcessDetector(bool projectZomboidRunning) : IProjectZomboidProcessDetector
    {
        public bool IsProjectZomboidRunning() => projectZomboidRunning;
    }
}
