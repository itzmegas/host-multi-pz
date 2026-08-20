using MultiHostPz.App.Cloud;
using MultiHostPz.App.Localization;

namespace MultiHostPz.App.Tests;

public sealed class CloudProviderTests
{
    [Theory]
    [InlineData(null, CloudProviderKind.GoogleDrive)]
    [InlineData("invalid", CloudProviderKind.GoogleDrive)]
    [InlineData("google-drive", CloudProviderKind.GoogleDrive)]
    [InlineData("dropbox", CloudProviderKind.Dropbox)]
    public void ProviderSelection_DefaultsSafely(string? value, CloudProviderKind expected) =>
        Assert.Equal(expected, CloudProviderSelection.Parse(value));

    [Fact]
    public void Settings_PersistsProviderAlongsideLanguageAndInvalidProviderDefaultsToGoogle()
    {
        using var directory = new DropboxOAuthTests.TestDirectory();
        var settings = new LanguageSettingsStore(directory.Path);
        settings.Save(AppLanguage.Spanish);
        settings.SaveCloudProvider(CloudProviderKind.Dropbox);
        Assert.Equal(AppLanguage.Spanish, settings.Load());
        Assert.Equal(CloudProviderKind.Dropbox, settings.LoadCloudProvider());
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"en\",\"CloudProvider\":\"bad\"}");
        Assert.Equal(CloudProviderKind.GoogleDrive, settings.LoadCloudProvider());
    }

    [Fact]
    public void Controller_GatesOnlySelectedProviderAndBusyDisablesEverything()
    {
        var google = new FakeProvider(CloudProviderKind.GoogleDrive, configured: false, connected: false);
        var dropbox = new FakeProvider(CloudProviderKind.Dropbox, configured: true, connected: true);
        var controller = new CloudProviderController([google, dropbox], CloudProviderKind.GoogleDrive);
        Assert.Equal((false, false), controller.Availability(false));
        controller.Select(CloudProviderKind.Dropbox);
        Assert.Equal((false, true), controller.Availability(false));
        Assert.Equal((false, false), controller.Availability(true));
    }

    [Fact]
    public void CloudStatuses_AreLocalizedPerProvider()
    {
        var english = new LocalizedText(AppLanguage.English);
        var spanish = new LocalizedText(AppLanguage.Spanish);
        Assert.Contains("Desktop OAuth client JSON", english.Format(new(CloudStatusKind.NotConfigured), CloudProviderKind.GoogleDrive));
        Assert.Contains("JSON de cliente OAuth de escritorio", spanish.Format(new(CloudStatusKind.NotConfigured), CloudProviderKind.GoogleDrive));
        Assert.Contains("Dropbox", english.Format(new(CloudStatusKind.Failed), CloudProviderKind.Dropbox));
    }

    private sealed class FakeProvider(CloudProviderKind kind, bool configured, bool connected) : ICloudProvider
    {
        public CloudProviderKind Kind => kind;
        public bool IsConfigured => configured;
        public bool IsConnected => connected;
        public string? AccountName => connected ? "account" : null;
        public ICloudSnapshotStore SnapshotStore { get; } = new FakeStore();
        public Task<string?> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("account");
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStore : ICloudSnapshotStore
    {
        public Task UploadAsync(string archivePath, string manifestPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudSnapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudSnapshot>>([]);
        public Task DownloadAsync(CloudSnapshot snapshot, string destinationDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
