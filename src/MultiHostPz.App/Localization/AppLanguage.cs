using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Localization;

public static class AppLanguage
{
    public const string English = "en";
    public const string Spanish = "es";

    public static string Select(CultureInfo systemCulture, string? savedLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);

        if (savedLanguage is English or Spanish)
        {
            return savedLanguage;
        }

        return systemCulture.TwoLetterISOLanguageName.Equals(Spanish, StringComparison.OrdinalIgnoreCase)
            ? Spanish
            : English;
    }

    public static CultureInfo Culture(string language) =>
        CultureInfo.GetCultureInfo(language == Spanish ? Spanish : English);
}

public sealed class LanguageSettingsStore
{
    private const string SettingsFileName = "settings.json";
    private readonly string _settingsPath;

    public LanguageSettingsStore(string? settingsDirectory = null)
    {
        var directory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiHostPz");
        _settingsPath = Path.Combine(Path.GetFullPath(directory), SettingsFileName);
    }

    public string? Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<LanguageSettings>(File.ReadAllText(_settingsPath));
            return settings?.Language is AppLanguage.English or AppLanguage.Spanish
                ? settings.Language
                : null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    public void Save(string language)
    {
        if (language is not (AppLanguage.English or AppLanguage.Spanish))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{SettingsFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new LanguageSettings(language));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record LanguageSettings(string Language);
}

public enum UiStatusKind
{
    NoSnapshot,
    RestoreCancelled,
    SnapshotCreated,
    SnapshotFailed,
    RestoreSucceeded,
    RestoreFailed
}

public sealed record UiStatus(
    UiStatusKind Kind,
    SnapshotFailureReason SnapshotFailure = SnapshotFailureReason.None,
    RestoreFailureReason RestoreFailure = RestoreFailureReason.None,
    string? SnapshotId = null,
    string? Path = null);

public enum CloudStatusKind
{
    NotConfigured, Disconnected, Connecting, Connected, UploadSucceeded, DownloadSucceeded, Failed
}

public sealed record CloudUiStatus(CloudStatusKind Kind, string? Value = null);

public sealed class LocalizedText
{
    private static readonly ResourceManager Resources = new(
        "MultiHostPz.App.Localization.Strings",
        typeof(LocalizedText).Assembly);

    public LocalizedText(string language)
    {
        Language = AppLanguage.Select(CultureInfo.InvariantCulture, language);
        Culture = AppLanguage.Culture(Language);
    }

    public string Language { get; }
    public CultureInfo Culture { get; }
    public string this[string key] => Resources.GetString(key, Culture) ?? key;

    public string Format(UiStatus status, string savesPath) => status.Kind switch
    {
        UiStatusKind.NoSnapshot => this["SnapshotNone"],
        UiStatusKind.RestoreCancelled => this["RestoreCancelled"],
        UiStatusKind.SnapshotCreated => Format("SnapshotCreated", status.SnapshotId, status.Path),
        UiStatusKind.SnapshotFailed => FormatSnapshotFailure(status.SnapshotFailure, savesPath),
        UiStatusKind.RestoreSucceeded => Format("RestoreSucceeded", status.SnapshotId, status.Path),
        UiStatusKind.RestoreFailed => FormatRestoreFailure(status, savesPath),
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public string Format(CloudUiStatus status) => status.Kind switch
    {
        CloudStatusKind.NotConfigured => this["DropboxNotConfigured"],
        CloudStatusKind.Disconnected => this["DropboxDisconnected"],
        CloudStatusKind.Connecting => this["DropboxConnecting"],
        CloudStatusKind.Connected => Format("DropboxConnected", status.Value),
        CloudStatusKind.UploadSucceeded => Format("DropboxUploadSucceeded", status.Value),
        CloudStatusKind.DownloadSucceeded => Format("DropboxDownloadSucceeded", status.Value),
        _ => this["DropboxFailed"]
    };

    private string FormatSnapshotFailure(SnapshotFailureReason reason, string savesPath) => reason switch
    {
        SnapshotFailureReason.ProjectZomboidRunning => this["SnapshotRunning"],
        SnapshotFailureReason.SourceMissing => Format("SnapshotSourceMissing", savesPath),
        SnapshotFailureReason.DestinationInsideSource => this["SnapshotDestinationInsideSource"],
        _ => this["SnapshotFailed"]
    };

    private string FormatRestoreFailure(UiStatus status, string savesPath)
    {
        var key = status.RestoreFailure switch
        {
            RestoreFailureReason.ProjectZomboidRunning => "RestoreRunning",
            RestoreFailureReason.TargetMissing => "RestoreTargetMissing",
            RestoreFailureReason.ArchiveMissing => "RestoreArchiveMissing",
            RestoreFailureReason.ManifestMissing => "RestoreManifestMissing",
            RestoreFailureReason.ManifestMalformed => "RestoreManifestMalformed",
            RestoreFailureReason.UnsupportedFormat => "RestoreUnsupportedFormat",
            RestoreFailureReason.ArchiveManifestIdentityMismatch => "RestoreIdentityMismatch",
            RestoreFailureReason.FileCountMismatch => "RestoreFileCountMismatch",
            RestoreFailureReason.ByteCountMismatch => "RestoreByteCountMismatch",
            RestoreFailureReason.InvalidZipEntry => "RestoreInvalidZip",
            RestoreFailureReason.NoValidSnapshot => "RestoreNoValidSnapshot",
            _ => status.Path is null ? "RestoreFailed" : "RestoreFailedBackup"
        };

        return key == "RestoreTargetMissing"
            ? Format(key, savesPath)
            : Format(key, status.SnapshotId ?? "?", status.Path ?? "?");
    }

    private string Format(string key, params object?[] values) =>
        string.Format(Culture, this[key], values);
}
