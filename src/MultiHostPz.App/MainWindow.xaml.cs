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
    private readonly DropboxOAuthClient _dropboxOAuth;
    private readonly CloudSnapshotTransferService _cloudTransfers;
    private LocalizedText _text = new(AppLanguage.English);
    private UiStatus _status = new(UiStatusKind.NoSnapshot);
    private CloudUiStatus _cloudStatus;
    private bool _cloudBusy;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        _snapshotService = new LocalSnapshotService();
        _snapshotRestoreService = new LocalSnapshotRestoreService();
        _saveLocation = new PzSaveLocator().Locate();
        _settingsStore = new LanguageSettingsStore();
        var http = new HttpClient();
        _dropboxOAuth = new DropboxOAuthClient(http, new DropboxTokenStore());
        _cloudTransfers = new CloudSnapshotTransferService(new DropboxSnapshotStore(http, _dropboxOAuth),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiHostPz", "snapshots"));
        _cloudStatus = !_dropboxOAuth.IsConfigured ? new(CloudStatusKind.NotConfigured)
            : _dropboxOAuth.Current is { } tokens ? new(CloudStatusKind.Connected, tokens.AccountName ?? "Dropbox")
            : new(CloudStatusKind.Disconnected);

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
        DropboxHeadingText.Text = _text["DropboxHeading"];
        DropboxConnectButton.Content = _text["DropboxConnectButton"];
        DropboxUploadButton.Content = _text["DropboxUploadButton"];
        DropboxDownloadButton.Content = _text["DropboxDownloadButton"];
        DropboxDisconnectButton.Content = _text["DropboxDisconnectButton"];
        RenderCloudStatus();
        RenderStatus();
    }

    private void RenderStatus()
    {
        SnapshotStatusText.Text = _text.Format(_status, _saveLocation.MultiplayerSavesPath);
    }

    private async void DropboxConnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync(async ct =>
        {
            _cloudStatus = new(CloudStatusKind.Connecting); RenderCloudStatus();
            var tokens = await _dropboxOAuth.ConnectAsync(ct);
            _cloudStatus = new(CloudStatusKind.Connected, tokens.AccountName ?? "Dropbox");
        });

    private async void DropboxUploadButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync(async ct => _cloudStatus = new(CloudStatusKind.UploadSucceeded,
            await _cloudTransfers.UploadLatestAsync(ct)));

    private async void DropboxDownloadButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync(async ct => _cloudStatus = new(CloudStatusKind.DownloadSucceeded,
            await _cloudTransfers.DownloadLatestAsync(ct)));

    private async void DropboxDisconnectButton_Click(object sender, RoutedEventArgs e) =>
        await RunCloudAsync(async ct => { await _dropboxOAuth.DisconnectAsync(ct); _cloudStatus = new(CloudStatusKind.Disconnected); });

    private async Task RunCloudAsync(Func<CancellationToken, Task> operation)
    {
        _cloudBusy = true; RenderCloudStatus();
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)); await operation(timeout.Token); }
        catch { _cloudStatus = new(CloudStatusKind.Failed); }
        finally { _cloudBusy = false; RenderCloudStatus(); }
    }

    private void RenderCloudStatus()
    {
        DropboxStatusText.Text = _text.Format(_cloudStatus);
        var connected = _dropboxOAuth.Current is not null;
        DropboxConnectButton.IsEnabled = CloudActionAvailability.CanConnect(
            _dropboxOAuth.IsConfigured, connected, _cloudBusy);
        DropboxUploadButton.IsEnabled = CloudActionAvailability.CanUseConnectedAction(
            _dropboxOAuth.IsConfigured, connected, _cloudBusy);
        DropboxDownloadButton.IsEnabled = CloudActionAvailability.CanUseConnectedAction(
            _dropboxOAuth.IsConfigured, connected, _cloudBusy);
        DropboxDisconnectButton.IsEnabled = CloudActionAvailability.CanUseConnectedAction(
            _dropboxOAuth.IsConfigured, connected, _cloudBusy);
    }
}
