using System.Xml.Linq;
using Xunit;

namespace costats.Core.Tests.Settings;

/// <summary>
/// The widget swaps whole theme dictionaries at runtime and every brush is a
/// DynamicResource, so a key missing from one palette only shows up as a
/// blank or default-coloured element in that palette.
/// </summary>
public sealed class ThemeDictionaryTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Palettes =
    [
        "ThemeLight.xaml",
        "ThemeDarkNeutralCharcoal.xaml",
        "ThemeDarkBlueSteel.xaml",
    ];

    [Fact]
    public void Every_theme_palette_defines_the_same_keys()
    {
        var keysByPalette = Palettes.ToDictionary(name => name, name => Keys(name));
        var expected = keysByPalette["ThemeLight.xaml"];

        Assert.NotEmpty(expected);
        foreach (var (name, keys) in keysByPalette)
        {
            Assert.True(
                expected.SetEquals(keys),
                $"{name} differs from ThemeLight.xaml. Missing: [{string.Join(", ", expected.Except(keys))}] " +
                $"Extra: [{string.Join(", ", keys.Except(expected))}]");
        }
    }

    [Fact]
    public void Theme_palettes_define_the_notice_resources()
    {
        foreach (var name in Palettes)
        {
            var keys = Keys(name);
            Assert.Contains("NoticeBgBrush", keys);
            Assert.Contains("NoticeStripeBrush", keys);
            Assert.Contains("NoticeStripeThickness", keys);
            Assert.Contains("NoticePadding", keys);
        }
    }

    private static HashSet<string> Keys(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "costats.App", "Themes", fileName);
        var document = XDocument.Load(path);
        return document.Root!
            .Elements()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "costats.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
