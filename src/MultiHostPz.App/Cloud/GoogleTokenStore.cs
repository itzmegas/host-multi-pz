using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace MultiHostPz.App.Cloud;

public sealed record GoogleTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc, string? AccountName = null);

public sealed class GoogleTokenStore
{
    private const string FileName = "google-drive.tokens";
    private readonly string _path;
    private readonly ISecretProtector _protector;

    public GoogleTokenStore(string? directory = null, ISecretProtector? protector = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiHostPz");
        _path = Path.Combine(Path.GetFullPath(directory), FileName);
        _protector = protector ?? new DpapiSecretProtector();
    }

    public GoogleTokens? Load()
    {
        try { return JsonSerializer.Deserialize<GoogleTokens>(_protector.Unprotect(File.ReadAllBytes(_path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
    }

    public void Save(GoogleTokens tokens)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens)));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
