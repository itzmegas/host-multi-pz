using System.IO.Compression;
using System.Text.Json;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Tests;

public sealed class LocalSnapshotServiceTests
{
    [Fact]
    public void CreateSnapshot_CreatesArchiveAndManifestWithoutChangingSource()
    {
        using var fixture = new SnapshotFixture();
        fixture.CreateSourceFile("server.ini", "server-name=Test Server");
        fixture.CreateSourceFile(Path.Combine("players", "alice.db"), "player-data");
        var sourceFilesBefore = ReadSourceFiles(fixture.SourceDirectory);

        var result = fixture.Service.CreateSnapshot(fixture.SourceDirectory);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.ArchivePath);
        Assert.NotNull(result.ManifestPath);
        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(File.Exists(result.ManifestPath));

        using (var archive = ZipFile.OpenRead(result.ArchivePath!))
        {
            Assert.Equal(
                ["players/alice.db", "server.ini"],
                archive.Entries.Select(entry => entry.FullName).OrderBy(path => path));
            Assert.Equal(
                "player-data",
                ReadArchiveEntry(archive, "players/alice.db"));
        }

        var manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(
            File.ReadAllText(result.ManifestPath!));

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(result.Manifest, manifest);
        Assert.Equal(TimeSpan.Zero, manifest.CreatedUtc.Offset);
        Assert.Equal(new DirectoryInfo(fixture.SourceDirectory).Name, manifest.SourceDirectoryName);
        Assert.Equal(2, manifest.FileCount);
        Assert.Equal("server-name=Test Server".Length + "player-data".Length, manifest.TotalBytes);
        Assert.Equal(Path.GetFileName(result.ArchivePath), manifest.ArchiveFileName);
        Assert.DoesNotContain(fixture.SourceDirectory, manifest.SourceDirectoryName, StringComparison.Ordinal);
        Assert.Equal(sourceFilesBefore, ReadSourceFiles(fixture.SourceDirectory));
    }

    [Fact]
    public void CreateSnapshot_WithServerIncludesSavesAndServerConfiguration()
    {
        using var fixture = new SnapshotFixture();
        fixture.CreateSourceFile(Path.Combine("players", "alice.db"), "player-data");
        fixture.CreateServerFile("server.ini", "server-name=Test Server");

        var result = fixture.Service.CreateSnapshot(fixture.SourceDirectory, fixture.ServerDirectory);

        Assert.True(result.Succeeded, result.ErrorMessage);
        using var archive = ZipFile.OpenRead(result.ArchivePath!);
        Assert.Contains("Saves/Multiplayer/players/alice.db", archive.Entries.Select(entry => entry.FullName));
        Assert.Contains("Server/server.ini", archive.Entries.Select(entry => entry.FullName));

        var manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(File.ReadAllText(result.ManifestPath!));
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.FormatVersion);
        Assert.Equal(LocalSnapshotManifest.SavesAndServerArchiveLayout, manifest.ArchiveLayout);
        Assert.Equal(2, manifest.FileCount);
    }

    [Fact]
    public void CreateSnapshot_RejectsMissingSource()
    {
        using var fixture = new SnapshotFixture();

        var result = fixture.Service.CreateSnapshot(
            Path.Combine(fixture.RootDirectory, "missing-source"));

        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotFailureReason.SourceMissing, result.FailureReason);
        Assert.Empty(Directory.GetFiles(fixture.RootDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CreateSnapshot_RejectsRunningProjectZomboid()
    {
        using var fixture = new SnapshotFixture(projectZomboidRunning: true);
        fixture.CreateSourceFile("server.ini", "server-name=Test Server");

        var result = fixture.Service.CreateSnapshot(fixture.SourceDirectory);

        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotFailureReason.ProjectZomboidRunning, result.FailureReason);
        Assert.Equal(
            ["server.ini"],
            Directory.GetFiles(fixture.SourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.SourceDirectory, path)));
    }

    [Fact]
    public void CreateSnapshot_RejectsDestinationInsideSource()
    {
        using var fixture = new SnapshotFixture();
        fixture.CreateSourceFile("server.ini", "server-name=Test Server");
        var destinationInsideSource = Path.Combine(fixture.SourceDirectory, "snapshots");
        var service = new LocalSnapshotService(
            new TestProcessDetector(false),
            destinationInsideSource);

        var result = service.CreateSnapshot(fixture.SourceDirectory);

        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotFailureReason.DestinationInsideSource, result.FailureReason);
        Assert.False(Directory.Exists(destinationInsideSource));
        Assert.Equal(
            ["server.ini"],
            Directory.GetFiles(fixture.SourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.SourceDirectory, path)));
    }

    [Fact]
    public void CreateSnapshot_CleansUpWhenDestinationCannotBeCreated()
    {
        using var fixture = new SnapshotFixture();
        fixture.CreateSourceFile("server.ini", "server-name=Test Server");
        var destinationFile = Path.Combine(fixture.RootDirectory, "destination-file");
        File.WriteAllText(destinationFile, "not a directory");
        var service = new LocalSnapshotService(
            new TestProcessDetector(false),
            destinationFile);

        var result = service.CreateSnapshot(fixture.SourceDirectory);

        Assert.False(result.Succeeded);
        Assert.Equal(SnapshotFailureReason.Other, result.FailureReason);
        Assert.Equal("not a directory", File.ReadAllText(destinationFile));
        Assert.Equal(
            ["destination-file", Path.Combine("source", "server.ini")],
            Directory.GetFiles(fixture.RootDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.RootDirectory, path))
                .OrderBy(path => path));
    }

    private static Dictionary<string, string> ReadSourceFiles(string sourceDirectory)
    {
        return Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(sourceDirectory, path),
                File.ReadAllText);
    }

    private static string ReadArchiveEntry(ZipArchive archive, string entryName)
    {
        using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
        return reader.ReadToEnd();
    }

    private sealed class SnapshotFixture : IDisposable
    {
        public SnapshotFixture(bool projectZomboidRunning = false)
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"MultiHostPzTests-{Guid.NewGuid():N}");
            SourceDirectory = Path.Combine(RootDirectory, "source");
            ServerDirectory = Path.Combine(RootDirectory, "server");
            SnapshotsDirectory = Path.Combine(RootDirectory, "snapshots");
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(ServerDirectory);
            Service = new LocalSnapshotService(
                new TestProcessDetector(projectZomboidRunning),
                SnapshotsDirectory);
        }

        public string RootDirectory { get; }

        public string SourceDirectory { get; }

        public string ServerDirectory { get; }

        public string SnapshotsDirectory { get; }

        public LocalSnapshotService Service { get; }

        public void CreateSourceFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(SourceDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void CreateServerFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(ServerDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
    }

    private sealed class TestProcessDetector(bool projectZomboidRunning) : IProjectZomboidProcessDetector
    {
        public bool IsProjectZomboidRunning() => projectZomboidRunning;
    }
}
