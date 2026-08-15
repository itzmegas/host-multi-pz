using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiHostPz.App.Services;

public enum SnapshotFailureReason
{
    None,
    SourceMissing,
    ProjectZomboidRunning,
    DestinationInsideSource,
    Other
}

public sealed record LocalSnapshotManifest(
    [property: JsonPropertyName("formatVersion")] int FormatVersion,
    [property: JsonPropertyName("snapshotId")] string SnapshotId,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("sourceDirectoryName")] string SourceDirectoryName,
    [property: JsonPropertyName("fileCount")] long FileCount,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("archiveFileName")] string ArchiveFileName);

public sealed record LocalSnapshotResult(
    bool Succeeded,
    string? ArchivePath,
    string? ManifestPath,
    LocalSnapshotManifest? Manifest,
    SnapshotFailureReason FailureReason,
    string? ErrorMessage)
{
    public static LocalSnapshotResult Success(
        string archivePath,
        string manifestPath,
        LocalSnapshotManifest manifest) =>
        new(true, archivePath, manifestPath, manifest, SnapshotFailureReason.None, null);

    public static LocalSnapshotResult Failure(
        SnapshotFailureReason reason,
        string errorMessage) =>
        new(false, null, null, null, reason, errorMessage);
}

public sealed class LocalSnapshotService
{
    private const int ManifestFormatVersion = 1;
    private const string SnapshotDirectoryName = "snapshots";
    private const string ApplicationDirectoryName = "MultiHostPz";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IProjectZomboidProcessDetector _processDetector;
    private readonly string _snapshotsDirectory;

    public LocalSnapshotService(
        IProjectZomboidProcessDetector? processDetector = null,
        string? snapshotsDirectory = null)
    {
        _processDetector = processDetector ?? new ProjectZomboidProcessDetector();
        _snapshotsDirectory = Path.GetFullPath(
            snapshotsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationDirectoryName,
                SnapshotDirectoryName));
    }

    public LocalSnapshotResult CreateSnapshot(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var resolvedSourceDirectory = Path.GetFullPath(sourceDirectory);
        var resolvedSnapshotsDirectory = Path.GetFullPath(_snapshotsDirectory);

        if (!Directory.Exists(resolvedSourceDirectory))
        {
            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.SourceMissing,
                "The multiplayer saves directory does not exist.");
        }

        if (_processDetector.IsProjectZomboidRunning())
        {
            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.ProjectZomboidRunning,
                "Project Zomboid is running. Close the game before creating a snapshot.");
        }

        if (IsSameOrDescendantPath(resolvedSourceDirectory, resolvedSnapshotsDirectory))
        {
            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.DestinationInsideSource,
                "The snapshot destination cannot be inside the multiplayer saves directory.");
        }

        string? temporaryArchivePath = null;
        string? temporaryManifestPath = null;
        string? archivePath = null;
        string? manifestPath = null;
        var archivePublished = false;
        var manifestPublished = false;

        try
        {
            Directory.CreateDirectory(resolvedSnapshotsDirectory);

            var snapshotId = Guid.NewGuid().ToString("N");
            var archiveFileName = $"snapshot-{snapshotId}.zip";
            var manifestFileName = $"snapshot-{snapshotId}.json";
            archivePath = Path.Combine(resolvedSnapshotsDirectory, archiveFileName);
            manifestPath = Path.Combine(resolvedSnapshotsDirectory, manifestFileName);
            temporaryArchivePath = Path.Combine(resolvedSnapshotsDirectory, $".{archiveFileName}.{Guid.NewGuid():N}.tmp");
            temporaryManifestPath = Path.Combine(resolvedSnapshotsDirectory, $".{manifestFileName}.{Guid.NewGuid():N}.tmp");

            var sourceFiles = Directory.EnumerateFiles(
                    resolvedSourceDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(filePath => new FileInfo(filePath))
                .ToArray();

            var manifest = new LocalSnapshotManifest(
                ManifestFormatVersion,
                snapshotId,
                DateTimeOffset.UtcNow,
                new DirectoryInfo(resolvedSourceDirectory).Name,
                sourceFiles.LongLength,
                sourceFiles.Sum(file => file.Length),
                archiveFileName);

            ZipFile.CreateFromDirectory(
                resolvedSourceDirectory,
                temporaryArchivePath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);

            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            using (var manifestStream = new FileStream(
                       temporaryManifestPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var manifestWriter = new StreamWriter(manifestStream))
            {
                manifestWriter.Write(manifestJson);
                manifestWriter.Flush();
                manifestStream.Flush(flushToDisk: true);
            }

            // Both temporary files are in the final directory, so each move is atomic on the same volume.
            File.Move(temporaryArchivePath, archivePath);
            archivePublished = true;
            temporaryArchivePath = null;

            File.Move(temporaryManifestPath, manifestPath);
            manifestPublished = true;
            temporaryManifestPath = null;

            return LocalSnapshotResult.Success(archivePath, manifestPath, manifest);
        }
        catch (Exception exception)
        {
            TryDeleteFile(temporaryArchivePath);
            TryDeleteFile(temporaryManifestPath);

            if (archivePublished)
            {
                TryDeleteFile(archivePath);
            }

            if (manifestPublished)
            {
                TryDeleteFile(manifestPath);
            }

            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.Other,
                exception.Message);
        }
    }

    private static bool IsSameOrDescendantPath(string sourceDirectory, string destinationDirectory)
    {
        var source = Path.TrimEndingDirectorySeparator(sourceDirectory);
        var destination = Path.TrimEndingDirectorySeparator(destinationDirectory);

        return destination.Equals(source, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(
                source + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(
                source + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original failure while making a best effort to remove temporary output.
        }
    }
}
