using System.Net.Http;
using MultiHostPz.App.Localization;

namespace MultiHostPz.App.Cloud;

public enum CloudProviderKind
{
    GoogleDrive,
    Dropbox
}

public static class CloudProviderSelection
{
    public const string GoogleDrive = "google-drive";
    public const string Dropbox = "dropbox";

    public static CloudProviderKind Parse(string? value) => value switch
    {
        Dropbox => CloudProviderKind.Dropbox,
        _ => CloudProviderKind.GoogleDrive
    };

    public static string Serialize(CloudProviderKind provider) => provider == CloudProviderKind.Dropbox
        ? Dropbox
        : GoogleDrive;
}

public interface ICloudProvider
{
    CloudProviderKind Kind { get; }
    bool IsConfigured { get; }
    bool IsConnected { get; }
    string? AccountName { get; }
    ICloudSnapshotStore SnapshotStore { get; }
    Task<string?> ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public sealed class DropboxCloudProvider : ICloudProvider
{
    private readonly DropboxOAuthClient _oauth;

    public DropboxCloudProvider(HttpClient http, DropboxOAuthClient oauth)
    {
        _oauth = oauth;
        SnapshotStore = new DropboxSnapshotStore(http, oauth);
    }

    public CloudProviderKind Kind => CloudProviderKind.Dropbox;
    public bool IsConfigured => _oauth.IsConfigured;
    public bool IsConnected => _oauth.Current is not null;
    public string? AccountName => _oauth.Current?.AccountName;
    public ICloudSnapshotStore SnapshotStore { get; }
    public async Task<string?> ConnectAsync(CancellationToken cancellationToken) =>
        (await _oauth.ConnectAsync(cancellationToken)).AccountName;
    public Task DisconnectAsync(CancellationToken cancellationToken) => _oauth.DisconnectAsync(cancellationToken);
}

public sealed class GoogleDriveCloudProvider : ICloudProvider
{
    private readonly GoogleOAuthClient _oauth;

    public GoogleDriveCloudProvider(HttpClient http, GoogleOAuthClient oauth)
    {
        _oauth = oauth;
        SnapshotStore = new GoogleDriveSnapshotStore(http, oauth);
    }

    public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;
    public bool IsConfigured => _oauth.IsConfigured;
    public bool IsConnected => _oauth.Current is not null;
    public string? AccountName => _oauth.Current?.AccountName;
    public ICloudSnapshotStore SnapshotStore { get; }
    public async Task<string?> ConnectAsync(CancellationToken cancellationToken) =>
        (await _oauth.ConnectAsync(cancellationToken)).AccountName;
    public Task DisconnectAsync(CancellationToken cancellationToken) => _oauth.DisconnectAsync(cancellationToken);
}

public sealed class CloudProviderController
{
    private readonly IReadOnlyDictionary<CloudProviderKind, ICloudProvider> _providers;

    public CloudProviderController(IEnumerable<ICloudProvider> providers, CloudProviderKind selected)
    {
        _providers = providers.ToDictionary(x => x.Kind);
        SelectedKind = selected;
    }

    public CloudProviderKind SelectedKind { get; private set; }
    public ICloudProvider Selected => _providers[SelectedKind];
    public void Select(CloudProviderKind provider) => SelectedKind = provider;

    public CloudUiStatus CurrentStatus() => !Selected.IsConfigured
        ? new(CloudStatusKind.NotConfigured)
        : Selected.IsConnected
            ? new(CloudStatusKind.Connected, Selected.AccountName ?? Selected.Kind.ToString())
            : new(CloudStatusKind.Disconnected);

    public (bool Connect, bool ConnectedActions) Availability(bool busy) =>
        (CloudActionAvailability.CanConnect(Selected.IsConfigured, Selected.IsConnected, busy),
            CloudActionAvailability.CanUseConnectedAction(Selected.IsConfigured, Selected.IsConnected, busy));
}
