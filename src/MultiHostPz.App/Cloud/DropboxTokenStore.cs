using System.Security.Cryptography;
using System.Text.Json;
using System.IO;

namespace MultiHostPz.App.Cloud;

public sealed record DropboxTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc, string? AccountName = null);

public interface ISecretProtector
{
    byte[] Protect(byte[] value);
    byte[] Unprotect(byte[] value);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] value) => ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] value) => ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);
}

public sealed class DropboxTokenStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;

    public DropboxTokenStore(string? directory = null, ISecretProtector? protector = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiHostPz");
        _path = Path.Combine(Path.GetFullPath(directory), "dropbox.tokens");
        _protector = protector ?? new DpapiSecretProtector();
    }

    public DropboxTokens? Load()
    {
        try { return JsonSerializer.Deserialize<DropboxTokens>(_protector.Unprotect(File.ReadAllBytes(_path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
    }

    public void Save(DropboxTokens tokens)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".dropbox.tokens.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens)));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Clear() { try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
