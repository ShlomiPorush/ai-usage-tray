using System.Windows;
using Microsoft.Win32;

namespace costats.App.Services;

/// <summary>
/// Global theme state readable from non-UI code (e.g. view-model colour pickers).
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; internal set; }
}

/// <summary>
/// Applies the light/dark resource dictionary. "system" follows the Windows
/// apps theme (AppsUseLightTheme) and reacts to it changing at runtime.
/// </summary>
public static class ThemeService
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    public static void Apply(string? theme)
    {
        var isDark = theme?.ToLowerInvariant() switch
        {
            DarkTheme => true,
            LightTheme => false,
            _ => IsSystemDark()
        };

        ThemeManager.IsDark = isDark;
        var source = new Uri($"pack://application:,,,/Themes/Theme{(isDark ? "Dark" : "Light")}.xaml");
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = new ResourceDictionary { Source = source };
        }
        else
        {
            dictionaries.Add(new ResourceDictionary { Source = source });
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
