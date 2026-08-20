namespace costats.App.ViewModels;

/// <summary>
/// One row in the Settings providers table. <see cref="AccountId"/> is set for
/// Claude/Codex accounts and null for the singleton Z.AI / Copilot providers.
/// For Claude/Codex, <see cref="Detail"/> holds the profile folder.
/// </summary>
public sealed record ProviderRowViewModel(string Kind, string? AccountId, string Name, string Detail)
{
    public string KindLabel => Kind switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "zai" => "Z.AI",
        _ => "Copilot"
    };
}
