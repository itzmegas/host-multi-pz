using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiHostPz.App.Cloud;

public static class DropboxPkce
{
    public static string CreateRandomValue(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    public static string CreateChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static bool StateMatches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class DropboxOAuthClient
{
    public const string RedirectUri = "http://127.0.0.1:53682/oauth/callback/";
    private readonly HttpClient _http;
    private readonly DropboxTokenStore _tokens;
    private readonly string? _appKey;

    public DropboxOAuthClient(HttpClient http, DropboxTokenStore tokens, string? appKey = null)
    {
        _http = http;
        _tokens = tokens;
        _appKey = string.IsNullOrWhiteSpace(appKey) ? Environment.GetEnvironmentVariable("MULTIHOSTPZ_DROPBOX_APP_KEY") : appKey;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_appKey);
    public DropboxTokens? Current => _tokens.Load();

    public async Task<DropboxTokens> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Dropbox is not configured.");
        var verifier = DropboxPkce.CreateRandomValue(64);
        var state = DropboxPkce.CreateRandomValue();
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        var authorization = "https://www.dropbox.com/oauth2/authorize?" + Form(new Dictionary<string, string>
        {
            ["client_id"] = _appKey!, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
            ["token_access_type"] = "offline", ["code_challenge_method"] = "S256",
            ["code_challenge"] = DropboxPkce.CreateChallenge(verifier), ["state"] = state,
            ["scope"] = "account_info.read files.metadata.read files.content.read files.content.write"
        });
        Process.Start(new ProcessStartInfo(authorization) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        DropboxTokens tokens;
        try
        {
            tokens = await CompleteAuthorizationAsync(context.Request.QueryString, state, verifier, cancellationToken);
        }
        catch
        {
            try { await OAuthLoopbackResponse.WriteAsync(context.Response, OAuthLoopbackResult.Failure()); }
            catch { context.Response.Close(); }
            throw new InvalidOperationException("Dropbox authorization failed safely.");
        }
        try { await OAuthLoopbackResponse.WriteAsync(context.Response, OAuthLoopbackResult.Success("Dropbox")); }
        catch { context.Response.Close(); }
        return tokens;
    }

    public async Task<DropboxTokens> CompleteAuthorizationAsync(System.Collections.Specialized.NameValueCollection query,
        string expectedState, string verifier, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Dropbox is not configured.");
        if (!DropboxPkce.StateMatches(expectedState, query["state"] ?? string.Empty) ||
            !string.IsNullOrWhiteSpace(query["error"]) || string.IsNullOrWhiteSpace(query["code"]))
            throw new InvalidOperationException("Dropbox authorization response was invalid.");

        var previousAccount = Current?.AccountName;
        var tokens = await ExchangeAsync(new Dictionary<string, string>
        {
            ["code"] = query["code"]!, ["grant_type"] = "authorization_code", ["client_id"] = _appKey!,
            ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidOperationException("Dropbox authorization did not return a refresh token.");

        var accountName = string.IsNullOrWhiteSpace(previousAccount) ? "Dropbox" : previousAccount;
        try { accountName = await GetAccountNameAsync(tokens.AccessToken, cancellationToken); }
        catch { }
        if (string.IsNullOrWhiteSpace(accountName)) accountName = "Dropbox";
        tokens = tokens with { AccountName = accountName };
        _tokens.Save(tokens);
        return tokens;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = Current ?? throw new InvalidOperationException("Dropbox is disconnected.");
        if (current.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return current.AccessToken;
        var refreshed = await ExchangeAsync(new Dictionary<string, string>
        {
            ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token", ["client_id"] = _appKey!
        }, cancellationToken, current.RefreshToken, current.AccountName);
        _tokens.Save(refreshed);
        return refreshed.AccessToken;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = Current;
            if (current is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/auth/token/revoke");
                request.Headers.Authorization = new("Bearer", current.AccessToken);
                await _http.SendAsync(request, cancellationToken);
            }
        }
        finally { _tokens.Clear(); }
    }

    private async Task<DropboxTokens> ExchangeAsync(Dictionary<string, string> values, CancellationToken ct,
        string? existingRefresh = null, string? accountName = null)
    {
        using var response = await _http.PostAsync("https://api.dropbox.com/oauth2/token", new FormUrlEncodedContent(values), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        return new(root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : existingRefresh!,
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), accountName);
    }

    private async Task<string> GetAccountNameAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
        request.Headers.Authorization = new("Bearer", accessToken);
        request.Content = new StringContent("null", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return json.RootElement.GetProperty("name").GetProperty("display_name").GetString() ?? "Dropbox";
    }

    private static string Form(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
}
