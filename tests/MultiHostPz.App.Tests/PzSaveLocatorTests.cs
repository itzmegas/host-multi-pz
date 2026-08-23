using MultiHostPz.App.Services;

namespace MultiHostPz.App.Tests;

public sealed class PzSaveLocatorTests
{
    [Fact]
    public void Locate_UsesInjectedUserProfileRoot()
    {
        const string userProfileRoot = @"C:\TestUsers\Alice";
        var locator = new PzSaveLocator(userProfileRoot);

        var location = locator.Locate();

        Assert.Equal(
            Path.Combine(userProfileRoot, "Zomboid"),
            location.ProfileRoot);
        Assert.Equal(
            Path.Combine(userProfileRoot, "Zomboid", "Saves", "Multiplayer"),
            location.MultiplayerSavesPath);
        Assert.Equal(
            Path.Combine(userProfileRoot, "Zomboid", "Server"),
            location.ServerPath);
    }

    [Fact]
    public void Locate_DoesNotRequireTheProfileToExist()
    {
        const string userProfileRoot = @"C:\PathThatDoesNotExist\Alice";
        var locator = new PzSaveLocator(userProfileRoot);

        var location = locator.Locate();

        Assert.Equal(
            Path.Combine(userProfileRoot, "Zomboid", "Saves", "Multiplayer"),
            location.MultiplayerSavesPath);
    }
}
