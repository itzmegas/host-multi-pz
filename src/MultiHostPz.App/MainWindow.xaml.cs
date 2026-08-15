using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MultiHostPz.App.Localization;
using MultiHostPz.App.Services;

namespace MultiHostPz.App;

public partial class MainWindow : Window
{
    private readonly LocalSnapshotService _snapshotService;
    private readonly LocalSnapshotRestoreService _snapshotRestoreService;
    private readonly PzSaveLocation _saveLocation;
    private readonly LanguageSettingsStore _settingsStore;
    private LocalizedText _text = new(AppLanguage.English);
    private UiStatus _status = new(UiStatusKind.NoSnapshot);
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        _snapshotService = new LocalSnapshotService();
        _snapshotRestoreService = new LocalSnapshotRestoreService();
        _saveLocation = new PzSaveLocator().Locate();
        _settingsStore = new LanguageSettingsStore();

        ProfileRootText.Text = _saveLocation.ProfileRoot;
        MultiplayerSavesPathText.Text = _saveLocation.MultiplayerSavesPath;
        var language = AppLanguage.Select(CultureInfo.CurrentUICulture, _settingsStore.Load());
        LanguageSelector.SelectedIndex = language == AppLanguage.Spanish ? 1 : 0;
        _initialized = true;
        ApplyLanguage(language);
    }

    private void CreateSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var result = _snapshotService.CreateSnapshot(_saveLocation.MultiplayerSavesPath);

        _status = result.Succeeded
            ? new UiStatus(UiStatusKind.SnapshotCreated, SnapshotId: result.Manifest!.SnapshotId, Path: result.ArchivePath)
            : new UiStatus(UiStatusKind.SnapshotFailed, SnapshotFailure: result.FailureReason);
        RenderStatus();
    }

    private void RestoreLatestSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = new RestoreConfirmationDialog(_text)
        {
            Owner = this
        }.ShowDialog();

        if (confirmation != true)
        {
            _status = new UiStatus(UiStatusKind.RestoreCancelled);
            RenderStatus();
            return;
        }

        var result = _snapshotRestoreService.RestoreLatestSnapshot(
            _saveLocation.MultiplayerSavesPath);

        _status = result.Succeeded
            ? new UiStatus(UiStatusKind.RestoreSucceeded, SnapshotId: result.SnapshotId, Path: result.BackupPath)
            : new UiStatus(UiStatusKind.RestoreFailed, RestoreFailure: result.FailureReason,
                SnapshotId: result.SnapshotId, Path: result.BackupPath);
        RenderStatus();
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguageSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string language)
        {
            return;
        }

        ApplyLanguage(language);
        try
        {
            _settingsStore.Save(language);
        }
        catch (IOException)
        {
            // The selected language remains active even when preferences cannot be persisted.
        }
        catch (UnauthorizedAccessException)
        {
            // The selected language remains active even when preferences cannot be persisted.
        }
    }

    private void ApplyLanguage(string language)
    {
        _text = new LocalizedText(language);
        CultureInfo.CurrentUICulture = _text.Culture;
        SubtitleText.Text = _text["Subtitle"];
        LanguageLabelText.Text = _text["LanguageLabel"];
        ProfileRootHeadingText.Text = _text["ProfileRootHeading"];
        SavesPathHeadingText.Text = _text["SavesPathHeading"];
        ProfileExistsLabelText.Text = _text["ProfileExistsLabel"];
        ProfileRootStatusText.Text = _text[Directory.Exists(_saveLocation.ProfileRoot) ? "Yes" : "No"];
        CreateSnapshotButton.Content = _text["CreateSnapshotButton"];
        RestoreSnapshotButton.Content = _text["RestoreButton"];
        SnapshotStatusHeadingText.Text = _text["SnapshotStatusHeading"];
        SyncStatusHeadingText.Text = _text["SyncStatusHeading"];
        SyncStatusText.Text = _text["SyncNone"];
        RenderStatus();
    }

    private void RenderStatus()
    {
        SnapshotStatusText.Text = _text.Format(_status, _saveLocation.MultiplayerSavesPath);
    }
}
