using System.Globalization;
using MultiHostPz.App.Localization;
using MultiHostPz.App.Services;

namespace MultiHostPz.App.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"MultiHostPzLocalizationTests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("es-AR", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")]
    public void Select_UsesSpanishForAnySpanishCultureOtherwiseEnglish(string culture, string expected)
    {
        Assert.Equal(expected, AppLanguage.Select(CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [InlineData("es-MX", "en", "en")]
    [InlineData("en-US", "es", "es")]
    public void Select_ExplicitLanguageOverridesSystemCulture(
        string culture,
        string savedLanguage,
        string expected)
    {
        Assert.Equal(expected, AppLanguage.Select(
            CultureInfo.GetCultureInfo(culture),
            savedLanguage));
    }

    [Fact]
    public void SettingsStore_RoundTripsExplicitLanguage()
    {
        var store = new LanguageSettingsStore(_temporaryDirectory);

        store.Save(AppLanguage.Spanish);

        Assert.Equal(AppLanguage.Spanish, store.Load());
    }

    [Fact]
    public void SettingsStore_MissingOrMalformedSettingsReturnNoChoice()
    {
        var store = new LanguageSettingsStore(_temporaryDirectory);
        Assert.Null(store.Load());

        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(Path.Combine(_temporaryDirectory, "settings.json"), "not-json");

        Assert.Null(store.Load());
        Assert.Equal(AppLanguage.Spanish, AppLanguage.Select(
            CultureInfo.GetCultureInfo("es-MX"), store.Load()));
    }

    [Fact]
    public void SettingsStore_UnsupportedLanguageReturnsNoChoice()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "settings.json"),
            "{\"Language\":\"fr\"}");
        var store = new LanguageSettingsStore(_temporaryDirectory);

        Assert.Null(store.Load());
        Assert.Equal(AppLanguage.English, AppLanguage.Select(
            CultureInfo.GetCultureInfo("fr-FR"), store.Load()));
    }

    [Fact]
    public void SettingsStore_UnreadableSettingsReturnNoChoice()
    {
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "settings.json"));
        var store = new LanguageSettingsStore(_temporaryDirectory);

        Assert.Null(store.Load());
        Assert.Equal(AppLanguage.English, AppLanguage.Select(
            CultureInfo.GetCultureInfo("de-DE"), store.Load()));
    }

    [Fact]
    public void Formatter_LocalizesStructuredSnapshotAndRestoreStatuses()
    {
        var english = new LocalizedText(AppLanguage.English);
        var spanish = new LocalizedText(AppLanguage.Spanish);
        var success = new UiStatus(UiStatusKind.RestoreSucceeded, SnapshotId: "abc123", Path: @"C:\backup");
        var failure = new UiStatus(UiStatusKind.RestoreFailed,
            RestoreFailure: RestoreFailureReason.ManifestMalformed, SnapshotId: "abc123");

        Assert.Equal("Snapshot abc123 was restored. The previous saves were preserved at C:\\backup.",
            english.Format(success, @"C:\saves"));
        Assert.Equal("Se restauró la instantánea abc123. Las partidas anteriores se conservaron en C:\\backup.",
            spanish.Format(success, @"C:\saves"));
        Assert.Contains("malformed manifest", english.Format(failure, @"C:\saves"));
        Assert.Contains("manifiesto con formato incorrecto", spanish.Format(failure, @"C:\saves"));
    }

    [Fact]
    public void Formatter_LocalizesActiveLocalOperations()
    {
        var english = new LocalizedText(AppLanguage.English);
        var spanish = new LocalizedText(AppLanguage.Spanish);

        Assert.Equal("Creating snapshot...", english.Format(new(UiStatusKind.CreatingSnapshot), "unused"));
        Assert.Equal("Restoring snapshot...", english.Format(new(UiStatusKind.RestoringSnapshot), "unused"));
        Assert.Equal("Creando instantánea...", spanish.Format(new(UiStatusKind.CreatingSnapshot), "unused"));
        Assert.Equal("Restaurando instantánea...", spanish.Format(new(UiStatusKind.RestoringSnapshot), "unused"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
