using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace MultiHostPz.App.Cloud;

public sealed record GoogleClientCredentials(string ClientId, string ClientSecret);

public static class GoogleClientCredentialsLoader
{
    private const string EmbeddedResourceName = "MultiHostPz.App.google-client-secrets.json";

    public static GoogleClientCredentials? Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("MULTIHOSTPZ_GOOGLE_CLIENT_SECRETS_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return LoadFile(path);
        }

        return LoadEmbedded();
    }

    private static GoogleClientCredentials? LoadFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.GetFullPath(path)));
            return Parse(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static GoogleClientCredentials? LoadEmbedded()
    {
        try
        {
            using var stream = typeof(GoogleClientCredentialsLoader).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null) return null;
            using var document = JsonDocument.Parse(stream);
            return Parse(document);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static GoogleClientCredentials? Parse(JsonDocument document)
    {
        var installed = document.RootElement.GetProperty("installed");
        var clientId = installed.GetProperty("client_id").GetString();
        var clientSecret = installed.GetProperty("client_secret").GetString();
        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            ? null
            : new(clientId, clientSecret);
    }
}

public static class GooglePkce
{
    public static string CreateRandomValue(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    public static string CreateChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static bool StateMatches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class GoogleOAuthClient
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive";
    private readonly HttpClient _http;
    private readonly GoogleTokenStore _tokens;
    private readonly GoogleClientCredentials? _credentials;

    public GoogleOAuthClient(HttpClient http, GoogleTokenStore tokens, GoogleClientCredentials? credentials = null)
    {
        _http = http;
        _tokens = tokens;
        _credentials = credentials ?? GoogleClientCredentialsLoader.Load();
    }

    public bool IsConfigured => _credentials is not null;
    public GoogleTokens? Current => _tokens.Load();

    public void ClearCachedTokens() => _tokens.Clear();

    public static Uri BuildAuthorizationUri(GoogleClientCredentials credentials, string redirectUri, string verifier, string state)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = DriveScope, ["access_type"] = "offline", ["prompt"] = "consent",
            ["code_challenge"] = GooglePkce.CreateChallenge(verifier), ["code_challenge_method"] = "S256", ["state"] = state
        };
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + Form(values));
    }

    public async Task<GoogleTokens> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_credentials is null) throw new InvalidOperationException("Google Drive is not configured.");
        var verifier = GooglePkce.CreateRandomValue(64);
        var state = GooglePkce.CreateRandomValue();
        var port = ReserveLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/oauth/callback/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        Process.Start(new ProcessStartInfo(BuildAuthorizationUri(_credentials, redirectUri, verifier, state).AbsoluteUri) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        GoogleTokens tokens;
        try
        {
            tokens = await CompleteAuthorizationAsync(context.Request.QueryString, state, verifier, redirectUri, cancellationToken);
        }
        catch
        {
            try { await OAuthLoopbackResponse.WriteAsync(context.Response, OAuthLoopbackResult.Failure()); }
            catch { context.Response.Close(); }
            throw new InvalidOperationException("Google Drive authorization failed safely.");
        }
        try { await OAuthLoopbackResponse.WriteAsync(context.Response, OAuthLoopbackResult.Success("Google Drive")); }
        catch { context.Response.Close(); }
        return tokens;
    }

    public async Task<GoogleTokens> CompleteAuthorizationAsync(System.Collections.Specialized.NameValueCollection query,
        string expectedState, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        if (_credentials is null) throw new InvalidOperationException("Google Drive is not configured.");
        if (!GooglePkce.StateMatches(expectedState, query["state"] ?? string.Empty) ||
            !string.IsNullOrWhiteSpace(query["error"]) || string.IsNullOrWhiteSpace(query["code"]))
            throw new InvalidOperationException("Google authorization response was invalid.");

        var previousAccount = Current?.AccountName;
        var tokens = await ExchangeAsync(new()
        {
            ["code"] = query["code"]!, ["client_id"] = _credentials.ClientId,
            ["client_secret"] = _credentials.ClientSecret, ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new InvalidOperationException("Google authorization did not return a refresh token.");

        var accountName = string.IsNullOrWhiteSpace(previousAccount) ? "Google Drive" : previousAccount;
        try { accountName = await GetAccountNameAsync(tokens.AccessToken, cancellationToken); }
        catch { }
        if (string.IsNullOrWhiteSpace(accountName)) accountName = "Google Drive";
        tokens = tokens with { AccountName = accountName };
        _tokens.Save(tokens);
        return tokens;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = Current ?? throw new InvalidOperationException("Google Drive is disconnected.");
        if (current.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return current.AccessToken;
        if (_credentials is null) throw new InvalidOperationException("Google Drive is not configured.");
        GoogleTokens refreshed;
        try
        {
            refreshed = await ExchangeAsync(new()
            {
                ["client_id"] = _credentials.ClientId, ["client_secret"] = _credentials.ClientSecret,
                ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token"
            }, cancellationToken, current.RefreshToken, current.AccountName);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            _tokens.Clear();
            throw;
        }
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
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = current.RefreshToken });
                await _http.PostAsync("https://oauth2.googleapis.com/revoke", content, cancellationToken);
            }
        }
        finally { _tokens.Clear(); }
    }

    private async Task<GoogleTokens> ExchangeAsync(Dictionary<string, string> values, CancellationToken ct,
        string? existingRefresh = null, string? accountName = null)
    {
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = json.RootElement;
        return new(root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : existingRefresh!,
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), accountName);
    }

    private async Task<string> GetAccountNameAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var user = json.RootElement.GetProperty("user");
        return user.TryGetProperty("emailAddress", out var email) && !string.IsNullOrWhiteSpace(email.GetString())
            ? email.GetString()!
            : user.GetProperty("displayName").GetString() ?? "Google Drive";
    }

    private static int ReserveLoopbackPort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static string Form(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
}
