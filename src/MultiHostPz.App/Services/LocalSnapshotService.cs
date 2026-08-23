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
    [property: JsonPropertyName("archiveFileName")] string ArchiveFileName,
    [property: JsonPropertyName("archiveLayout")] string ArchiveLayout = "multiplayer")
{
    public const string LegacyArchiveLayout = "multiplayer";
    public const string SavesAndServerArchiveLayout = "saves-and-server";
}

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
    private const int LegacyManifestFormatVersion = 1;
    private const int CombinedManifestFormatVersion = 2;
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

    public LocalSnapshotResult CreateSnapshot(string sourceDirectory, string? serverDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var resolvedSourceDirectory = Path.GetFullPath(sourceDirectory);
        var resolvedSnapshotsDirectory = Path.GetFullPath(_snapshotsDirectory);
        string? resolvedServerDirectory = null;

        if (!Directory.Exists(resolvedSourceDirectory))
        {
            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.SourceMissing,
                "The multiplayer saves directory does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(serverDirectory))
        {
            resolvedServerDirectory = Path.GetFullPath(serverDirectory);
            if (!Directory.Exists(resolvedServerDirectory))
            {
                return LocalSnapshotResult.Failure(
                    SnapshotFailureReason.SourceMissing,
                    "The server configuration directory does not exist.");
            }
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

        if (resolvedServerDirectory is not null
            && IsSameOrDescendantPath(resolvedServerDirectory, resolvedSnapshotsDirectory))
        {
            return LocalSnapshotResult.Failure(
                SnapshotFailureReason.DestinationInsideSource,
                "The snapshot destination cannot be inside the server configuration directory.");
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
            var serverFiles = resolvedServerDirectory is null
                ? []
                : Directory.EnumerateFiles(resolvedServerDirectory, "*", SearchOption.AllDirectories)
                    .Select(filePath => new FileInfo(filePath))
                    .ToArray();

            var manifest = new LocalSnapshotManifest(
                resolvedServerDirectory is null ? LegacyManifestFormatVersion : CombinedManifestFormatVersion,
                snapshotId,
                DateTimeOffset.UtcNow,
                resolvedServerDirectory is null ? new DirectoryInfo(resolvedSourceDirectory).Name : "Zomboid",
                sourceFiles.LongLength + serverFiles.LongLength,
                sourceFiles.Sum(file => file.Length) + serverFiles.Sum(file => file.Length),
                archiveFileName,
                resolvedServerDirectory is null
                    ? LocalSnapshotManifest.LegacyArchiveLayout
                    : LocalSnapshotManifest.SavesAndServerArchiveLayout);

            CreateArchive(
                resolvedSourceDirectory,
                resolvedServerDirectory,
                temporaryArchivePath,
                resolvedServerDirectory is not null);

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

    private static void CreateArchive(
        string sourceDirectory,
        string? serverDirectory,
        string archivePath,
        bool includeServer)
    {
        if (!includeServer)
        {
            ZipFile.CreateFromDirectory(
                sourceDirectory,
                archivePath,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);
            return;
        }

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        AddDirectoryToArchive(archive, sourceDirectory, "Saves/Multiplayer");
        AddDirectoryToArchive(archive, serverDirectory!, "Server");
    }

    private static void AddDirectoryToArchive(
        ZipArchive archive,
        string sourceDirectory,
        string archiveRoot)
    {
        var root = archiveRoot.TrimEnd('/') + "/";
        archive.CreateEntry(root);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory).Replace('\\', '/');
            archive.CreateEntry($"{root}{relativePath}/");
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            var entry = archive.CreateEntry($"{root}{relativePath}", CompressionLevel.Optimal);
            using var input = File.OpenRead(file);
            using var output = entry.Open();
            input.CopyTo(output);
        }
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
