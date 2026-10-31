using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public static class ProviderCatalog
{
    public static ProviderProfile Codex { get; } = new("codex", "Codex", "#0A84FF");
    public static ProviderProfile Claude { get; } = new("claude", "Claude", "#FF7A00");
    public static ProviderProfile Copilot { get; } = new("copilot", "Copilot", "#6E40C9");
    public static ProviderProfile Antigravity { get; } = new("antigravity", "Antigravity", "#4285F4");
}
