using System.Net;
using System.Text;
using MultiHostPz.App.Cloud;

namespace MultiHostPz.App.Tests;

public sealed class DropboxOAuthTests
{
    [Fact]
    public void Pkce_ProducesValidChallengeAndValidatesState()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", DropboxPkce.CreateChallenge(verifier));
        Assert.True(DropboxPkce.StateMatches("expected", "expected"));
        Assert.False(DropboxPkce.StateMatches("expected", "changed"));
    }

    [Fact]
    public void TokenStore_RoundTripsAndFailsClosedForCorruptState()
    {
        using var directory = new TestDirectory();
        var store = new DropboxTokenStore(directory.Path, new ReversingProtector());
        var tokens = new DropboxTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Account");
        store.Save(tokens);
        Assert.Equal(tokens, store.Load());
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "dropbox.tokens"), "corrupt");
        Assert.Null(store.Load());
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshedWithoutClientSecret()
    {
        using var directory = new TestDirectory();
        var store = new DropboxTokenStore(directory.Path, new ReversingProtector());
        store.Save(new("old-access", "refresh-value", DateTimeOffset.UtcNow.AddMinutes(-1)));
        string? body = null;
        var http = new HttpClient(new StubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"expires_in\":14400}");
        }));
        var oauth = new DropboxOAuthClient(http, store, "test-app-key");

        Assert.Equal("new-access", await oauth.GetAccessTokenAsync(default));
        Assert.Contains("refresh_token=refresh-value", body);
        Assert.Contains("client_id=test-app-key", body);
        Assert.DoesNotContain("client_secret", body);
        Assert.Equal("refresh-value", store.Load()!.RefreshToken);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.Reverse().ToArray();
        public byte[] Unprotect(byte[] value) => value.Reverse().ToArray();
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    internal sealed class TestDirectory : IDisposable
    {
        public TestDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"MultiHostPzCloudTests-{Guid.NewGuid():N}"); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
