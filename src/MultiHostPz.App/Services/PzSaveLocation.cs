using System.IO;

namespace MultiHostPz.App.Services;

public sealed record PzSaveLocation(string ProfileRoot, string MultiplayerSavesPath, string ServerPath);

public sealed class PzSaveLocator
{
    private const string ZomboidDirectoryName = "Zomboid";
    private const string SavesDirectoryName = "Saves";
    private const string MultiplayerDirectoryName = "Multiplayer";

    private readonly string _userProfileRoot;

    public PzSaveLocator()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public PzSaveLocator(string userProfileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileRoot);
        _userProfileRoot = Path.GetFullPath(userProfileRoot);
    }

    public PzSaveLocation Locate()
    {
        var profileRoot = Path.Combine(_userProfileRoot, ZomboidDirectoryName);
        var multiplayerSavesPath = Path.Combine(
            profileRoot,
            SavesDirectoryName,
            MultiplayerDirectoryName);
        var serverPath = Path.Combine(profileRoot, "Server");

        return new PzSaveLocation(profileRoot, multiplayerSavesPath, serverPath);
    }
}
