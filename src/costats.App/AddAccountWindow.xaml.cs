using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace costats.App;

/// <summary>
/// Modal dialog for adding a monitored account. Shows only the fields the
/// selected provider needs: profile folder for Claude/Codex, API key for Z.AI.
/// </summary>
public partial class AddAccountWindow : Window
{
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

    /// <summary>"claude", "codex" or "zai".</summary>
    public string ProviderType { get; private set; } = "claude";

    public string AccountName => NameBox.Text.Trim();
    public string ConfigDir => FolderBox.Text.Trim();
    public string ZaiApiKey => ZaiKeyBox.Password.Trim();

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
                ZaiPanel.Visibility = Visibility.Collapsed;
                FolderLabel.Text = "Profile folder (CLAUDE_CONFIG_DIR)";
                FolderHint.Text = "The folder holding the Claude Code login. Use ~/.claude for your main login, or a dedicated folder for an extra account (sign in with: CLAUDE_CONFIG_DIR=<folder> claude).";
                if (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".codex"))
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
                ZaiPanel.Visibility = Visibility.Collapsed;
                FolderLabel.Text = "Profile folder (CODEX_HOME)";
                FolderHint.Text = "The folder holding the Codex CLI login. Use ~/.codex for your main login, or a dedicated folder for an extra account (sign in with: CODEX_HOME=<folder> codex login).";
                if (string.IsNullOrWhiteSpace(FolderBox.Text) || FolderBox.Text.Contains(".claude"))
                {
                    FolderBox.Text = Path.Combine(home, ".codex");
                }
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == "Claude")
                {
                    NameBox.Text = "Codex";
                }
                break;

            default: // zai
                FolderPanel.Visibility = Visibility.Collapsed;
                ZaiPanel.Visibility = Visibility.Visible;
                if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text is "Claude" or "Codex")
                {
                    NameBox.Text = "GLM";
                }
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
            "zai" when ZaiApiKey.Length == 0 => "Enter the Z.AI API key.",
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
