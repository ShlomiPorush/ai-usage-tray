using System.Windows.Input;
using costats.Application.Settings;
using Microsoft.Extensions.Logging;
using NHotkey.Wpf;

namespace costats.App.Services
{
    public sealed class HotkeyService : IDisposable
    {
        private const string HotkeyName = "ToggleWidget";
        private readonly ILogger<HotkeyService> _logger;
        private bool _registered;

        public HotkeyService(AppSettings settings, ILogger<HotkeyService> logger)
        {
            _logger = logger;
            Apply(settings.HotkeyEnabled, settings.Hotkey);
        }

        public event EventHandler? ToggleRequested;

        public HotkeyApplyResult Apply(bool enabled, string? hotkey)
        {
            if (!enabled)
            {
                RemoveRegistration();
                return HotkeyApplyResult.Success(string.Empty);
            }

            if (!TryParseHotkey(hotkey, out var key, out var modifiers, out var normalized))
            {
                return HotkeyApplyResult.Failure("Use a modifier and one key, for example Ctrl+Alt+U.");
            }

            try
            {
                HotkeyManager.Current.AddOrReplace(
                    HotkeyName,
                    key,
                    modifiers,
                    (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
                _registered = true;
                return HotkeyApplyResult.Success(normalized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register hotkey");
                return HotkeyApplyResult.Failure("Windows could not register this shortcut. It may already be in use.");
            }
        }

        public void Dispose()
        {
            RemoveRegistration();
        }

        private void RemoveRegistration()
        {
            if (!_registered)
            {
                return;
            }

            try
            {
                HotkeyManager.Current.Remove(HotkeyName);
                _registered = false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to unregister hotkey");
            }
        }

        private static bool TryParseHotkey(
            string? hotkey,
            out Key key,
            out ModifierKeys modifiers,
            out string normalized)
        {
            key = Key.None;
            modifiers = ModifierKeys.None;
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                return false;
            }

            foreach (var part in hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = part.Trim();
                if (token.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Control;
                    continue;
                }

                if (token.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Alt;
                    continue;
                }

                if (token.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Shift;
                    continue;
                }

                if (token.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierKeys.Windows;
                    continue;
                }

                if (key != Key.None || !Enum.TryParse(token, true, out Key parsed) || parsed == Key.None)
                {
                    return false;
                }

                key = parsed;
            }

            if (key == Key.None || modifiers == ModifierKeys.None)
            {
                return false;
            }

            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            normalized = string.Join('+', parts);

            return true;
        }
    }

    public sealed record HotkeyApplyResult(bool IsSuccess, string NormalizedHotkey, string Error)
    {
        public static HotkeyApplyResult Success(string normalized) => new(true, normalized, string.Empty);
        public static HotkeyApplyResult Failure(string error) => new(false, string.Empty, error);
    }
}
