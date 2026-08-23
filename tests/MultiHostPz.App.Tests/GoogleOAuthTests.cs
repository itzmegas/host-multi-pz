using System.Net;
using System.Text;
using System.Web;
using MultiHostPz.App.Cloud;
using static MultiHostPz.App.Tests.DropboxOAuthTests;

namespace MultiHostPz.App.Tests;

public sealed class GoogleOAuthTests
{
    [Fact]
    public void CredentialsLoader_ParsesInstalledClientAndFailsClosed()
    {
        using var directory = new TestDirectory();
        var valid = Path.Combine(directory.Path, "client.json");
        File.WriteAllText(valid, "{\"installed\":{\"client_id\":\"desktop-id\",\"client_secret\":\"desktop-secret\"}}");
        Assert.Equal(new GoogleClientCredentials("desktop-id", "desktop-secret"), GoogleClientCredentialsLoader.Load(valid));
        File.WriteAllText(valid, "{\"web\":{\"client_id\":\"wrong-kind\"}}");
        Assert.Null(GoogleClientCredentialsLoader.Load(valid));
        Assert.Null(GoogleClientCredentialsLoader.Load(Path.Combine(directory.Path, "missing.json")));
    }

    [Fact]
    public void AuthorizationUri_UsesPkceStateOfflineDriveFileAndLoopback()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var uri = GoogleOAuthClient.BuildAuthorizationUri(new("id", "secret"), "http://127.0.0.1:49152/oauth/callback/", verifier, "state-value");
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state-value", query["state"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal(GoogleOAuthClient.DriveScope, query["scope"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"]);
        Assert.True(GooglePkce.StateMatches("same", "same"));
        Assert.False(GooglePkce.StateMatches("same", "other"));
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshedAndRetainsRefreshTokenAndAccount()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        store.Save(new("old", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1), "user@example.test"));
        string? body = null;
        var http = new HttpClient(new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"expires_in\":3600}");
        }));
        var oauth = new GoogleOAuthClient(http, store, new("id", "secret"));
        Assert.Equal("new", await oauth.GetAccessTokenAsync(default));
        Assert.Contains("refresh_token=refresh", body);
        Assert.Contains("client_secret=secret", body);
        Assert.Equal("refresh", store.Load()!.RefreshToken);
        Assert.Equal("user@example.test", store.Load()!.AccountName);
    }

    [Fact]
    public async Task ExpiredToken_InvalidGrantClearsCachedTokens()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        store.Save(new("old", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var http = new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"))));
        var oauth = new GoogleOAuthClient(http, store, new("id", "secret"));

        await Assert.ThrowsAsync<HttpRequestException>(() => oauth.GetAccessTokenAsync(default));

        Assert.Null(store.Load());
    }

    [Fact]
    public void TokenStore_IsIsolatedAtomicAndFailsClosedForCorruption()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        var tokens = new GoogleTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "account");
        store.Save(tokens);
        Assert.Equal(tokens, store.Load());
        Assert.True(File.Exists(Path.Combine(directory.Path, "google-drive.tokens")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "dropbox.tokens")));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
        File.WriteAllText(Path.Combine(directory.Path, "google-drive.tokens"), "corrupt");
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Completion_AccountLookupFailureStillPersistsFallbackConnection()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        var http = new HttpClient(new StubHandler(request => Task.FromResult(request.RequestUri!.Host == "oauth2.googleapis.com"
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}")
            : Json(HttpStatusCode.ServiceUnavailable, "{}"))));

        var result = await new GoogleOAuthClient(http, store, new("id", "secret"))
            .CompleteAuthorizationAsync(Query("state", "code"), "state", "verifier", "http://127.0.0.1/callback/", default);

        Assert.Equal("Google Drive", result.AccountName);
        Assert.Equal(result, store.Load());
        var provider = new GoogleDriveCloudProvider(http, new GoogleOAuthClient(http, store, new("id", "secret")));
        var availability = new CloudProviderController([provider], CloudProviderKind.GoogleDrive).Availability(false);
        Assert.Equal((false, true), availability);
    }

    [Fact]
    public async Task Completion_AccountLookupSuccessPersistsIdentity()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        var http = new HttpClient(new StubHandler(request => Task.FromResult(request.RequestUri!.Host == "oauth2.googleapis.com"
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}")
            : Json(HttpStatusCode.OK, "{\"user\":{\"emailAddress\":\"user@example.test\"}}"))));

        var result = await new GoogleOAuthClient(http, store, new("id", "secret"))
            .CompleteAuthorizationAsync(Query("state", "code"), "state", "verifier", "http://127.0.0.1/callback/", default);

        Assert.Equal("user@example.test", result.AccountName);
        Assert.Equal("user@example.test", store.Load()!.AccountName);
    }

    [Theory]
    [InlineData("{\"access_token\":\"access\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"access\",\"refresh_token\":\"\",\"expires_in\":3600}")]
    public async Task Completion_MissingRefreshTokenFailsClosed(string tokenJson)
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        var oauth = new GoogleOAuthClient(new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, tokenJson)))), store, new("id", "secret"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteAuthorizationAsync(
            Query("state", "code"), "state", "verifier", "http://127.0.0.1/callback/", default));
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Completion_PersistenceFailureFailsConnection()
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ThrowingProtector());
        var oauth = new GoogleOAuthClient(new HttpClient(new StubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}")))), store, new("id", "secret"));

        await Assert.ThrowsAsync<IOException>(() => oauth.CompleteAuthorizationAsync(
            Query("state", "code"), "state", "verifier", "http://127.0.0.1/callback/", default));
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("wrong", null)]
    [InlineData("state", "access_denied")]
    public async Task Completion_InvalidCallbackStoresNothing(string state, string? error)
    {
        using var directory = new TestDirectory();
        var store = new GoogleTokenStore(directory.Path, new ReversingProtector());
        var calls = 0;
        var oauth = new GoogleOAuthClient(new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); })), store, new("id", "secret"));
        var query = Query(state, "provider-secret-code");
        if (error is not null) { query["error"] = error; query["error_description"] = "provider supplied arbitrary text"; }

        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteAuthorizationAsync(
            query, "state", "verifier", "http://127.0.0.1/callback/", default));
        Assert.Equal(0, calls);
        Assert.Null(store.Load());
    }

    [Fact]
    public void LoopbackMessages_AreSafeAndSuccessIsExplicit()
    {
        var success = OAuthLoopbackResult.Success("Google Drive");
        var failure = OAuthLoopbackResult.Failure();
        Assert.Equal(200, success.StatusCode);
        Assert.Contains("connected successfully", success.Message);
        Assert.Equal(400, failure.StatusCode);
        Assert.DoesNotContain("token", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static System.Collections.Specialized.NameValueCollection Query(string state, string code) =>
        new() { ["state"] = state, ["code"] = code };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.Reverse().ToArray();
        public byte[] Unprotect(byte[] value) => value.Reverse().ToArray();
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => throw new IOException("Test persistence failure.");
        public byte[] Unprotect(byte[] value) => value;
    }
}
