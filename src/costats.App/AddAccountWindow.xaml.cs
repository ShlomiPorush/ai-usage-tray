using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace costats.App;

/// <summary>
/// Modal dialog for adding or editing a monitored provider. Shows only the
/// fields the selected provider needs: profile folder for Claude/Codex, an API
/// key for Z.AI, a personal access token for Copilot.
/// </summary>
public partial class AddAccountWindow : Window
{
    private readonly bool _isEditMode;

    public AddAccountWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        UpdateFieldsForProvider();
    }

    /// <summary>Creates the dialog in edit mode, prefilled and locked to one provider.</summary>
    public AddAccountWindow(string providerType, string name, string? configDir) : this()
    {
        _isEditMode = true;
        TitleText.Text = "Edit account";
        ConfirmButton.Content = "Save";

        foreach (ComboBoxItem item in ProviderBox.Items)
        {
            if ((string)item.Tag == providerType)
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        }
        ProviderBox.IsEnabled = false;

        NameBox.Text = name;
        if (configDir is not null)
        {
            FolderBox.Text = configDir;
        }
        UpdateFieldsForProvider();
    }

    /// <summary>"claude", "codex", "zai" or "copilot".</summary>
    public string ProviderType { get; private set; } = "claude";

    public string AccountName => NameBox.Text.Trim();
    public string ConfigDir => FolderBox.Text.Trim();

    /// <summary>API key / token. In edit mode an empty value means "keep the existing secret".</summary>
    public string Secret => SecretBox.Password.Trim();

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateFieldsForProvider();

    private void UpdateFieldsForProvider()
    {
        if (FolderPanel is null)
        {
            return; // fired during InitializeComponent before panels exist
        }

        ProviderType = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "claude";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        switch (ProviderType)
        {
            case "claude":
                FolderPanel.Visibility = Visibility.Visible;
                SecretPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Visible;
                FolderLabel.Text = "Profile folder (CLAUDE_CONFIG_DIR)";
                FolderHint.Text = "The folder holding the Claude Code login. Use ~/.claude for your main login, or a dedicated folder for an extra account (sign in with: CLAUDE_CONFIG_DIR=<folder> claude).";
                if (!_isEditMode && (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".codex")))
                {
                    FolderBox.Text = Path.Combine(home, ".claude");
                }
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                {
                    NameBox.Text = "Claude";
                }
                break;

            case "codex":
                FolderPanel.Visibility = Visibility.Visible;
                SecretPanel.Visibility = Visibility.Collapsed;
                NamePanel.Visibility = Visibility.Visible;
                FolderLabel.Text = "Profile folder (CODEX_HOME)";
                FolderHint.Text = "The folder holding the Codex CLI login. Use ~/.codex for your main login, or a dedicated folder for an extra account (sign in with: CODEX_HOME=<folder> codex login).";
                if (!_isEditMode && (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".claude")))
                {
                    FolderBox.Text = Path.Combine(home, ".codex");
                }
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Claude")
                {
                    NameBox.Text = "Codex";
                }
                break;

            case "zai":
                FolderPanel.Visibility = Visibility.Collapsed;
                SecretPanel.Visibility = Visibility.Visible;
                NamePanel.Visibility = Visibility.Visible;
                SecretLabel.Text = "API key";
                SecretHint.Text = _isEditMode
                    ? "Coding-plan key from z.ai/manage-apikey. Leave empty to keep the current key."
                    : "Coding-plan key from z.ai/manage-apikey. Stored in settings.json.";
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text is "Claude" or "Codex")
                {
                    NameBox.Text = "GLM";
                }
                break;

            default: // copilot
                FolderPanel.Visibility = Visibility.Collapsed;
                SecretPanel.Visibility = Visibility.Visible;
                NamePanel.Visibility = Visibility.Collapsed;
                SecretLabel.Text = "Personal access token";
                SecretHint.Text = _isEditMode
                    ? "Classic GitHub token with the copilot and read:user scopes. Leave empty to keep the current token. Stored in Windows Credential Manager."
                    : "Classic GitHub token with the copilot and read:user scopes. Stored in Windows Credential Manager, not in settings.json.";
                break;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the account's profile folder",
            InitialDirectory = Directory.Exists(ConfigDir)
                ? ConfigDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        string? error = ProviderType switch
        {
            "zai" when Secret.Length == 0 && !_isEditMode => "Enter the Z.AI API key.",
            "copilot" when Secret.Length == 0 && !_isEditMode => "Enter the GitHub personal access token.",
            "claude" or "codex" when ConfigDir.Length == 0 => "Choose a profile folder.",
            _ => null
        };

        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
