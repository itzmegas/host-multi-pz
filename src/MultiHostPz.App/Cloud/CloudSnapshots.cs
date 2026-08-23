using System.Text.Json;
using System.IO;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Cloud;

public sealed record CloudSnapshot(string SnapshotId, DateTimeOffset CreatedUtc, string ArchiveName, string ManifestName,
    string? ArchiveId = null, string? ManifestId = null);

public static class CloudActionAvailability
{
    public static bool CanConnect(bool configured, bool connected, bool busy) =>
        configured && !connected && !busy;

    public static bool CanUseConnectedAction(bool configured, bool connected, bool busy) =>
        configured && connected && !busy;
}

public interface ICloudSnapshotStore
{
    Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken);
    Task DownloadAsync(CloudSnapshot snapshot, string destinationDirectory, CancellationToken cancellationToken);
}

public sealed class CloudSnapshotTransferService
{
    private readonly ICloudSnapshotStore _store;
    private readonly LocalSnapshotRestoreService _validator;
    private readonly IProjectZomboidProcessDetector _processDetector;
    private readonly string _snapshotsDirectory;

    public CloudSnapshotTransferService(ICloudSnapshotStore store, string snapshotsDirectory,
        IProjectZomboidProcessDetector? processDetector = null)
    {
        _store = store;
        _snapshotsDirectory = Path.GetFullPath(snapshotsDirectory);
        _processDetector = processDetector ?? new ProjectZomboidProcessDetector();
        _validator = new LocalSnapshotRestoreService(processDetector, _snapshotsDirectory);
    }

    public async Task<string> UploadLatestAsync(CancellationToken cancellationToken)
    {
        var snapshot = FindLatestValidLocalSnapshot()
            ?? throw new InvalidOperationException("No valid local snapshot was found.");
        await _store.UploadAsync(snapshot.ArchivePath, snapshot.ManifestPath, cancellationToken);
        return snapshot.Manifest.SnapshotId;
    }

    public async Task<string> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        var snapshot = (await _store.ListAsync(cancellationToken)).OrderByDescending(x => x.CreatedUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("No valid remote snapshot was found.");
        var staging = Path.Combine(Path.GetTempPath(), $"MultiHostPzCloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await _store.DownloadAsync(snapshot, staging, cancellationToken);
            var archive = Path.Combine(staging, snapshot.ArchiveName);
            var manifest = Path.Combine(staging, snapshot.ManifestName);
            var validation = _validator.ValidateSnapshot(archive, manifest);
            if (!validation.Succeeded)
            {
                throw new InvalidDataException(validation.SafeMessage);
            }

            Directory.CreateDirectory(_snapshotsDirectory);
            var finalArchive = Path.Combine(_snapshotsDirectory, snapshot.ArchiveName);
            var finalManifest = Path.Combine(_snapshotsDirectory, snapshot.ManifestName);
            if (File.Exists(finalArchive) || File.Exists(finalManifest))
            {
                throw new IOException("The snapshot already exists locally.");
            }

            File.Move(archive, finalArchive);
            try
            {
                File.Move(manifest, finalManifest);
            }
            catch
            {
                File.Delete(finalArchive);
                throw;
            }
            return snapshot.SnapshotId;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    public Task<LocalSnapshotRestoreResult> DownloadAndRestoreLatestAsync(
        string? targetDirectory,
        CancellationToken cancellationToken) =>
        DownloadAndRestoreLatestAsync(targetDirectory, null, cancellationToken);

    public async Task<LocalSnapshotRestoreResult> DownloadAndRestoreLatestAsync(
        string? targetDirectory,
        string? serverDirectory,
        CancellationToken cancellationToken)
    {
        if (_processDetector.IsProjectZomboidRunning())
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ProjectZomboidRunning,
                "Project Zomboid is running. Close the game before restoring a snapshot.");
        }

        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.TargetMissing,
                "The multiplayer saves directory does not exist.");
        }

        var snapshotId = await DownloadLatestAsync(cancellationToken);
        return _validator.RestoreSnapshot(
            targetDirectory,
            Path.Combine(_snapshotsDirectory, $"snapshot-{snapshotId}.zip"),
            Path.Combine(_snapshotsDirectory, $"snapshot-{snapshotId}.json"),
            serverDirectory);
    }

    private (string ArchivePath, string ManifestPath, LocalSnapshotManifest Manifest)? FindLatestValidLocalSnapshot()
    {
        if (!Directory.Exists(_snapshotsDirectory)) return null;
        return Directory.EnumerateFiles(_snapshotsDirectory, "snapshot-*.json")
            .Select(path => TryRead(path))
            .Where(x => x is not null)
            .OrderByDescending(x => x!.Value.Manifest.CreatedUtc)
            .FirstOrDefault(x => _validator.ValidateSnapshot(x!.Value.ArchivePath, x.Value.ManifestPath).Succeeded);
    }

    private static (string ArchivePath, string ManifestPath, LocalSnapshotManifest Manifest)? TryRead(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(File.ReadAllText(path));
            return manifest is null ? null : (Path.Combine(Path.GetDirectoryName(path)!, manifest.ArchiveFileName), path, manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
