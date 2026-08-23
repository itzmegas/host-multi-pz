using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using MultiHostPz.App.Localization;
using MultiHostPz.App.Services;
using MultiHostPz.App.Cloud;

namespace MultiHostPz.App;

public partial class MainWindow : Window
{
    private readonly LocalSnapshotService _snapshotService;
    private readonly LocalSnapshotRestoreService _snapshotRestoreService;
    private readonly PzSaveLocation _saveLocation;
    private readonly LanguageSettingsStore _settingsStore;
    private readonly CloudProviderController _cloudProviders;
    private readonly string _snapshotsDirectory;
    private LocalizedText _text = new(AppLanguage.English);
    private UiStatus _status = new(UiStatusKind.NoSnapshot);
    private CloudUiStatus _cloudStatus;
    private readonly UiOperationGate _operationGate = new();
    private string _operationMessageKey = "OperationInProgress";
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        _snapshotService = new LocalSnapshotService();
        _snapshotRestoreService = new LocalSnapshotRestoreService();
        _saveLocation = new PzSaveLocator().Locate();
        _settingsStore = new LanguageSettingsStore();
        var http = new HttpClient();
        var dropbox = new DropboxCloudProvider(http, new DropboxOAuthClient(http, new DropboxTokenStore()));
        var google = new GoogleDriveCloudProvider(http, new GoogleOAuthClient(http, new GoogleTokenStore()));
        _cloudProviders = new([google, dropbox], _settingsStore.LoadCloudProvider());
        _snapshotsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiHostPz", "snapshots");
        _cloudStatus = _cloudProviders.CurrentStatus();

        ProfileRootText.Text = _saveLocation.ProfileRoot;
        MultiplayerSavesPathText.Text = _saveLocation.MultiplayerSavesPath;
        var language = AppLanguage.Select(CultureInfo.CurrentUICulture, _settingsStore.Load());
        LanguageSelector.SelectedIndex = language == AppLanguage.Spanish ? 1 : 0;
        CloudProviderSelector.SelectedIndex = _cloudProviders.SelectedKind == CloudProviderKind.GoogleDrive ? 0 : 1;
        _initialized = true;
        ApplyLanguage(language);
    }

    private void CloudProviderSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || CloudProviderSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string provider) return;
        _cloudProviders.Select(CloudProviderSelection.Parse(provider));
        _cloudStatus = _cloudProviders.CurrentStatus();
        try { _settingsStore.SaveCloudProvider(_cloudProviders.SelectedKind); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        RenderCloudStatus();
    }

    private async void CreateSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_operationGate.TryBegin(UiOperation.Local)) return;

        _operationMessageKey = "OperationCreatingSnapshot";
        _status = new UiStatus(UiStatusKind.CreatingSnapshot);
        RenderStatus();
        LocalSnapshotResult result;
        try
        {
            result = await Task.Run(() => _snapshotService.CreateSnapshot(
                _saveLocation.MultiplayerSavesPath,
                _saveLocation.ServerPath));
        }
        catch
        {
            result = LocalSnapshotResult.Failure(SnapshotFailureReason.Other, "Snapshot creation failed safely.");
        }
        finally
        {
            _operationGate.End(UiOperation.Local);
            _operationMessageKey = "OperationInProgress";
            RenderActionAvailability();
        }

        _status = result.Succeeded
            ? new UiStatus(UiStatusKind.SnapshotCreated, SnapshotId: result.Manifest!.SnapshotId, Path: result.ArchivePath)
            : new UiStatus(UiStatusKind.SnapshotFailed, SnapshotFailure: result.FailureReason);
        RenderStatus();
    }

    private async void RestoreLatestSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationGate.Current != UiOperation.Idle) return;

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

        if (!_operationGate.TryBegin(UiOperation.Local)) return;

        _operationMessageKey = "OperationRestoringSnapshot";
        _status = new UiStatus(UiStatusKind.RestoringSnapshot);
        RenderStatus();
        LocalSnapshotRestoreResult result;
        try
        {
            result = await Task.Run(() => _snapshotRestoreService.RestoreLatestSnapshot(
                _saveLocation.MultiplayerSavesPath,
                _saveLocation.ServerPath));
        }
        catch
        {
            result = LocalSnapshotRestoreResult.Failure(
                RestoreFailureReason.RestoreFailed,
                "Snapshot restore failed safely.");
        }
        finally
        {
            _operationGate.End(UiOperation.Local);
            _operationMessageKey = "OperationInProgress";
            RenderActionAvailability();
        }

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
        OpenProfileFolderButton.Content = _text["OpenProfileFolderButton"];
        OpenSavesFolderButton.Content = _text["OpenSavesFolderButton"];
        ProfileExistsLabelText.Text = _text["ProfileExistsLabel"];
        ProfileRootStatusText.Text = _text[Directory.Exists(_saveLocation.ProfileRoot) ? "Yes" : "No"];
        CreateSnapshotButton.Content = _text["CreateSnapshotButton"];
        RestoreSnapshotButton.Content = _text["RestoreButton"];
        SnapshotStatusHeadingText.Text = _text["SnapshotStatusHeading"];
        SyncStatusHeadingText.Text = _text["SyncStatusHeading"];
        SyncStatusText.Text = _text["SyncNone"];
        CloudHeadingText.Text = _text["CloudHeading"];
        CloudProviderLabelText.Text = _text["CloudProviderLabel"];
        CloudConnectButton.Content = _text["CloudConnectButton"];
        CloudUploadButton.Content = _text["CloudUploadButton"];
        CloudDownloadButton.Content = _text["CloudDownloadButton"];
        CloudDisconnectButton.Content = _text["CloudDisconnectButton"];
        RenderCloudStatus();
        RenderStatus();
    }

    private void RenderStatus()
    {
        SnapshotStatusText.Text = _text.Format(_status, _saveLocation.MultiplayerSavesPath);
        RenderActionAvailability();
    }

    private void OpenProfileFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_saveLocation.ProfileRoot);

    private void OpenSavesFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_saveLocation.MultiplayerSavesPath);

    private static void OpenFolder(string path)
    {
        try
        {
            var folder = Path.GetFullPath(path);
            while (!Directory.Exists(folder))
            {
                var parent = Directory.GetParent(folder)?.FullName;
                if (parent is null) return;
                folder = parent;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (Win32Exception) { }
    }

    private async void CloudConnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync("OperationConnecting", async ct =>
        {
            _cloudStatus = new(CloudStatusKind.Connecting); RenderCloudStatus();
            var account = await _cloudProviders.Selected.ConnectAsync(ct);
            _cloudStatus = new(CloudStatusKind.Connected, account ?? _cloudProviders.SelectedKind.ToString());
        });

    private async void CloudUploadButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync("OperationUploading", async ct => _cloudStatus = new(CloudStatusKind.UploadSucceeded,
            await Transfers().UploadLatestAsync(ct)));

    private async void CloudDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationGate.Current != UiOperation.Idle) return;

        var confirmation = new RestoreConfirmationDialog(
            _text,
            "ConfirmDownloadRestoreTitle",
            "ConfirmDownloadRestoreMessage")
        {
            Owner = this
        }.ShowDialog();

        if (confirmation != true)
        {
            _status = new UiStatus(UiStatusKind.RestoreCancelled);
            RenderStatus();
            return;
        }

        await RunCloudAsync("OperationDownloading", async ct =>
        {
            var result = await Transfers().DownloadAndRestoreLatestAsync(
                _saveLocation.MultiplayerSavesPath,
                _saveLocation.ServerPath,
                ct);
            _cloudStatus = result.Succeeded
                ? new(CloudStatusKind.DownloadRestored, result.SnapshotId)
                : new(CloudStatusKind.Failed);
            _status = result.Succeeded
                ? new UiStatus(UiStatusKind.RestoreSucceeded, SnapshotId: result.SnapshotId, Path: result.BackupPath)
                : new UiStatus(UiStatusKind.RestoreFailed, RestoreFailure: result.FailureReason,
                    SnapshotId: result.SnapshotId, Path: result.BackupPath);
            RenderStatus();
        });
    }

    private async void CloudDisconnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync("OperationDisconnecting", async ct => { await _cloudProviders.Selected.DisconnectAsync(ct); _cloudStatus = new(CloudStatusKind.Disconnected); });

    private CloudSnapshotTransferService Transfers() => new(_cloudProviders.Selected.SnapshotStore, _snapshotsDirectory);

    private async Task RunCloudAsync(string operationMessageKey, Func<CancellationToken, Task> operation)
    {
        if (!_operationGate.TryBegin(UiOperation.Cloud)) return;
        _operationMessageKey = operationMessageKey;
        RenderCloudStatus();
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)); await operation(timeout.Token); }
        catch { _cloudStatus = new(CloudStatusKind.Failed); }
        finally
        {
            _operationGate.End(UiOperation.Cloud);
            _operationMessageKey = "OperationInProgress";
            RenderCloudStatus();
        }
    }

    private void RenderCloudStatus()
    {
        CloudStatusText.Text = _text.Format(_cloudStatus, _cloudProviders.SelectedKind);
        RenderActionAvailability();
    }

    private void RenderActionAvailability()
    {
        var busy = _operationGate.Current != UiOperation.Idle;
        OperationOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OperationProgressText.Text = _text[_operationMessageKey];

        var availability = UiActionAvailabilityCalculator.Calculate(
            _cloudProviders.Selected.IsConfigured,
            _cloudProviders.Selected.IsConnected,
            _operationGate.Current);
        CreateSnapshotButton.IsEnabled = availability.CreateSnapshot;
        RestoreSnapshotButton.IsEnabled = availability.RestoreSnapshot;
        CloudProviderSelector.IsEnabled = availability.SelectCloudProvider;
        CloudConnectButton.IsEnabled = availability.ConnectCloud;
        CloudUploadButton.IsEnabled = availability.UseConnectedCloudAction;
        CloudDownloadButton.IsEnabled = availability.UseConnectedCloudAction;
        CloudDisconnectButton.IsEnabled = availability.UseConnectedCloudAction;
    }
}
