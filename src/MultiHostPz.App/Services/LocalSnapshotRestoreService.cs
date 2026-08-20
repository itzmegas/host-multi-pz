using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MultiHostPz.App.Services;

public enum RestoreFailureReason
{
    None,
    ProjectZomboidRunning,
    TargetMissing,
    ArchiveMissing,
    ManifestMissing,
    ManifestMalformed,
    UnsupportedFormat,
    ArchiveManifestIdentityMismatch,
    FileCountMismatch,
    ByteCountMismatch,
    InvalidZipEntry,
    NoValidSnapshot,
    RestoreFailed
}

public sealed record LocalSnapshotRestoreResult(
    bool Succeeded,
    string? SnapshotId,
    string? BackupPath,
    RestoreFailureReason FailureReason,
    string SafeMessage)
{
    public static LocalSnapshotRestoreResult Success(string snapshotId, string backupPath) =>
        new(true, snapshotId, backupPath, RestoreFailureReason.None,
            $"Snapshot {snapshotId} was restored. The previous target was preserved at {backupPath}.");

    public static LocalSnapshotRestoreResult Failure(
        RestoreFailureReason reason,
        string safeMessage,
        string? snapshotId = null,
        string? backupPath = null) =>
        new(false, snapshotId, backupPath, reason, safeMessage);
}

public sealed class LocalSnapshotRestoreService
{
    private const int SupportedManifestFormatVersion = 1;
    private const string SnapshotDirectoryName = "snapshots";
    private const string ApplicationDirectoryName = "MultiHostPz";

    private readonly IProjectZomboidProcessDetector _processDetector;
    private readonly string _snapshotsDirectory;

    public LocalSnapshotRestoreService(
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

    public LocalSnapshotRestoreResult RestoreSnapshot(
        string? targetDirectory,
        string? archivePath,
        string? manifestPath)
    {
        if (!TryResolveExistingDirectory(targetDirectory, out var resolvedTargetDirectory))
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.TargetMissing,
                "The multiplayer saves directory does not exist.");
        }

        if (_processDetector.IsProjectZomboidRunning())
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ProjectZomboidRunning,
                "Project Zomboid is running. Close the game before restoring a snapshot.");
        }

        if (!TryValidateSnapshot(archivePath, manifestPath, out var validatedSnapshot, out var failure))
        {
            return failure!;
        }

        return RestoreValidatedSnapshot(resolvedTargetDirectory, validatedSnapshot!);
    }

    public LocalSnapshotRestoreResult ValidateSnapshot(string? archivePath, string? manifestPath)
    {
        return TryValidateSnapshot(archivePath, manifestPath, out var snapshot, out var failure)
            ? new LocalSnapshotRestoreResult(true, snapshot!.Manifest.SnapshotId, null,
                RestoreFailureReason.None, "The snapshot is valid.")
            : failure!;
    }

    public LocalSnapshotRestoreResult RestoreLatestSnapshot(string? targetDirectory)
    {
        if (!TryResolveExistingDirectory(targetDirectory, out var resolvedTargetDirectory))
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.TargetMissing,
                "The multiplayer saves directory does not exist.");
        }

        if (_processDetector.IsProjectZomboidRunning())
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ProjectZomboidRunning,
                "Project Zomboid is running. Close the game before restoring a snapshot.");
        }

        if (!Directory.Exists(_snapshotsDirectory))
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.NoValidSnapshot,
                "No valid local snapshot was found.");
        }

        var candidates = new List<(string ManifestPath, LocalSnapshotManifest Manifest)>();
        try
        {
            foreach (var candidateManifestPath in Directory.EnumerateFiles(
                         _snapshotsDirectory,
                         "snapshot-*.json",
                         SearchOption.TopDirectoryOnly))
            {
            if (TryReadManifest(candidateManifestPath, out var manifest))
                {
                    candidates.Add((candidateManifestPath, manifest!));
                }
            }
        }
        catch (IOException)
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.NoValidSnapshot,
                "No valid local snapshot was found.");
        }
        catch (UnauthorizedAccessException)
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.NoValidSnapshot,
                "No valid local snapshot was found.");
        }

        foreach (var candidate in candidates.OrderByDescending(
                     candidate => candidate.Manifest.CreatedUtc))
        {
            var archivePath = Path.Combine(
                _snapshotsDirectory,
                Path.GetFileNameWithoutExtension(candidate.ManifestPath) + ".zip");

            if (!TryValidateSnapshot(
                    archivePath,
                    candidate.ManifestPath,
                    out var validatedSnapshot,
                    out _))
            {
                continue;
            }

            return RestoreValidatedSnapshot(resolvedTargetDirectory, validatedSnapshot!);
        }

        return LocalSnapshotRestoreResult.Failure(
            RestoreFailureReason.NoValidSnapshot,
            "No valid local snapshot was found.");
    }

    private static bool TryResolveExistingDirectory(
        string? path,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            resolvedPath = Path.GetFullPath(path);
            return Directory.Exists(resolvedPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryValidateSnapshot(
        string? archivePath,
        string? manifestPath,
        out ValidatedSnapshot? validatedSnapshot,
        out LocalSnapshotRestoreResult? failure)
    {
        validatedSnapshot = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ManifestMissing,
                "The snapshot manifest is missing.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ArchiveMissing,
                "The snapshot archive is missing.");
            return false;
        }

        if (!TryReadManifest(manifestPath!, out var manifest))
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ManifestMalformed,
                "The snapshot manifest is malformed.");
            return false;
        }

        var snapshotId = manifest!.SnapshotId;
        if (manifest.FormatVersion == 0
            || string.IsNullOrWhiteSpace(manifest.SnapshotId)
            || !Guid.TryParseExact(manifest.SnapshotId, "N", out _)
            || manifest.CreatedUtc == default
            || string.IsNullOrWhiteSpace(manifest.SourceDirectoryName)
            || manifest.FileCount < 0
            || manifest.TotalBytes < 0
            || string.IsNullOrWhiteSpace(manifest.ArchiveFileName))
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ManifestMalformed,
                "The snapshot manifest is malformed.",
                snapshotId);
            return false;
        }

        if (manifest.FormatVersion != SupportedManifestFormatVersion)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.UnsupportedFormat,
                "The snapshot uses an unsupported format.",
                snapshotId);
            return false;
        }

        var expectedArchiveFileName = $"snapshot-{manifest.SnapshotId}.zip";
        var expectedManifestFileName = $"snapshot-{manifest.SnapshotId}.json";
        if (!string.Equals(manifest.ArchiveFileName, expectedArchiveFileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(manifestPath), expectedManifestFileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(archivePath), expectedArchiveFileName, StringComparison.Ordinal))
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.ArchiveManifestIdentityMismatch,
                "The snapshot archive and manifest identities do not match.",
                snapshotId);
            return false;
        }

        validatedSnapshot = new ValidatedSnapshot(
            Path.GetFullPath(archivePath!),
            Path.GetFullPath(manifestPath),
            manifest);

        if (!TryValidateArchive(validatedSnapshot, out failure))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadManifest(
        string manifestPath,
        out LocalSnapshotManifest? manifest)
    {
        manifest = null;

        try
        {
            manifest = JsonSerializer.Deserialize<LocalSnapshotManifest>(
                File.ReadAllText(manifestPath));
            return manifest is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryValidateArchive(
        ValidatedSnapshot snapshot,
        out LocalSnapshotRestoreResult? failure)
    {
        failure = null;

        try
        {
            using var archive = ZipFile.OpenRead(snapshot.ArchivePath);
            var validationRoot = Path.Combine(
                Path.GetTempPath(),
                $"MultiHostPzValidation-{Guid.NewGuid():N}");
            var entries = ValidateEntries(archive, validationRoot);
            if (entries is null)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.InvalidZipEntry,
                    "The snapshot contains an invalid ZIP entry.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            var fileCount = entries.Count(entry => !entry.IsDirectory);
            if (fileCount != snapshot.Manifest.FileCount)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.FileCountMismatch,
                    "The snapshot file count does not match its manifest.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            var totalBytes = entries
                .Where(entry => !entry.IsDirectory)
                .Sum(entry => entry.Entry.Length);
            if (totalBytes != snapshot.Manifest.TotalBytes)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.ByteCountMismatch,
                    "The snapshot byte count does not match its manifest.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.InvalidZipEntry,
                "The snapshot archive is not a valid ZIP archive.",
                snapshot.Manifest.SnapshotId);
            return false;
        }
        catch (IOException)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.InvalidZipEntry,
                "The snapshot archive could not be read.",
                snapshot.Manifest.SnapshotId);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.InvalidZipEntry,
                "The snapshot archive could not be read.",
                snapshot.Manifest.SnapshotId);
            return false;
        }
    }

    private static List<ValidatedEntry>? ValidateEntries(
        ZipArchive archive,
        string extractionRoot)
    {
        extractionRoot = Path.GetFullPath(extractionRoot);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ValidatedEntry>();

        foreach (var entry in archive.Entries)
        {
            if (!TryGetSafeEntryPath(extractionRoot, entry.FullName, out var fullPath))
            {
                return null;
            }

            if (!seenPaths.Add(fullPath))
            {
                return null;
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            if (!isDirectory)
            {
                filePaths.Add(fullPath);
            }

            entries.Add(new ValidatedEntry(entry, fullPath, isDirectory));
        }

        foreach (var filePath in filePaths)
        {
            var parentPath = Directory.GetParent(filePath)?.FullName;
            while (parentPath is not null
                   && !string.Equals(parentPath, extractionRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (filePaths.Contains(parentPath))
                {
                    return null;
                }

                parentPath = Directory.GetParent(parentPath)?.FullName;
            }
        }

        return entries;
    }

    private static bool TryGetSafeEntryPath(
        string extractionRoot,
        string entryName,
        out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(entryName)
            || entryName.Contains('\0')
            || Path.IsPathRooted(entryName))
        {
            return false;
        }

        var normalizedEntryName = entryName.Replace('\\', '/');
        if (normalizedEntryName is "." or "./"
            || normalizedEntryName.StartsWith("/", StringComparison.Ordinal)
            || normalizedEntryName.Split('/')[0].Contains(':', StringComparison.Ordinal)
            || normalizedEntryName.Split('/').Any(segment => segment == ".."))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(
                extractionRoot,
                normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
            return IsSameOrDescendantPath(extractionRoot, fullPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static LocalSnapshotRestoreResult RestoreValidatedSnapshot(
        string targetDirectory,
        ValidatedSnapshot snapshot)
    {
        var targetParent = Directory.GetParent(targetDirectory)?.FullName;
        if (targetParent is null)
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.RestoreFailed,
                "The restore target has no usable parent directory.",
                snapshot.Manifest.SnapshotId);
        }

        var targetName = new DirectoryInfo(targetDirectory).Name;
        var temporaryDirectory = Path.Combine(
            targetParent,
            $".{targetName}.restore-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(
            targetParent,
            $"{targetName}.backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            if (!TryExtractArchive(snapshot, temporaryDirectory, out var extractionFailure))
            {
                return extractionFailure!;
            }

            Directory.Move(targetDirectory, backupPath);
            try
            {
                Directory.Move(temporaryDirectory, targetDirectory);
                temporaryDirectory = string.Empty;
                return LocalSnapshotRestoreResult.Success(
                    snapshot.Manifest.SnapshotId,
                    backupPath);
            }
            catch
            {
                TryDeleteDirectory(targetDirectory);
                try
                {
                    CopyDirectory(backupPath, targetDirectory);
                    return LocalSnapshotRestoreResult.Failure(
                        RestoreFailureReason.RestoreFailed,
                        $"The restore failed and the original target was rolled back. The backup was retained at {backupPath}.",
                        snapshot.Manifest.SnapshotId,
                        backupPath);
                }
                catch
                {
                    return LocalSnapshotRestoreResult.Failure(
                        RestoreFailureReason.RestoreFailed,
                        $"The restore failed and rollback could not complete. The original backup was retained at {backupPath}.",
                        snapshot.Manifest.SnapshotId,
                        backupPath);
                }
            }
        }
        catch (IOException)
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.RestoreFailed,
                "The snapshot could not replace the target directory.",
                snapshot.Manifest.SnapshotId,
                Directory.Exists(backupPath) ? backupPath : null);
        }
        catch (UnauthorizedAccessException)
        {
            return LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.RestoreFailed,
                "The snapshot could not replace the target directory.",
                snapshot.Manifest.SnapshotId,
                Directory.Exists(backupPath) ? backupPath : null);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static bool TryExtractArchive(
        ValidatedSnapshot snapshot,
        string temporaryDirectory,
        out LocalSnapshotRestoreResult? failure)
    {
        failure = null;

        try
        {
            using var archive = ZipFile.OpenRead(snapshot.ArchivePath);
            var entries = ValidateEntries(archive, temporaryDirectory);
            if (entries is null)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.InvalidZipEntry,
                    "The snapshot contains an invalid ZIP entry.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            var fileCount = entries.Count(entry => !entry.IsDirectory);
            var totalBytes = entries
                .Where(entry => !entry.IsDirectory)
                .Sum(entry => entry.Entry.Length);
            if (fileCount != snapshot.Manifest.FileCount)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.FileCountMismatch,
                    "The snapshot file count does not match its manifest.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            if (totalBytes != snapshot.Manifest.TotalBytes)
            {
                failure = LocalSnapshotRestoreResult.Failure(
                    RestoreFailureReason.ByteCountMismatch,
                    "The snapshot byte count does not match its manifest.",
                    snapshot.Manifest.SnapshotId);
                return false;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(entry.FullPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entry.FullPath)!);
                using var source = entry.Entry.Open();
                using var destination = new FileStream(
                    entry.FullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                source.CopyTo(destination);
            }

            return true;
        }
        catch (InvalidDataException)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.InvalidZipEntry,
                "The snapshot archive is not a valid ZIP archive.",
                snapshot.Manifest.SnapshotId);
            return false;
        }
        catch (IOException)
        {
            failure = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.RestoreFailed,
                "The snapshot could not be extracted safely.",
                snapshot.Manifest.SnapshotId);
            return false;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                targetDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var targetFile = Path.Combine(
                targetDirectory,
                Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile);
        }
    }

    private static bool IsSameOrDescendantPath(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                root + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the restore result while making a best effort to remove staging output.
        }
    }

    private sealed record ValidatedSnapshot(
        string ArchivePath,
        string ManifestPath,
        LocalSnapshotManifest Manifest);

    private sealed record ValidatedEntry(
        ZipArchiveEntry Entry,
        string FullPath,
        bool IsDirectory);
}
