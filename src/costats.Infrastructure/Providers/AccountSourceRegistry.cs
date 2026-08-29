using costats.Application.Pulse;
using costats.Application.SessionActivation;
using costats.Application.Settings;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Builds one signal source per configured account (Claude or Codex) from
/// <see cref="AppSettings.GetEffectiveAccounts"/>. <see cref="Reload"/> swaps the
/// list atomically, so a refresh already in flight keeps its own snapshot.
/// </summary>
public sealed class AccountSourceRegistry : IAccountSourceRegistry
{
    private readonly AppSettings _settings;
    private readonly ICodexAppServerClient _codexClient;
    private readonly ISessionActivationWindowRegistry _windowRegistry;
    private volatile IReadOnlyList<ISignalSource> _current = [];

    public AccountSourceRegistry(
        AppSettings settings,
        ICodexAppServerClient codexClient,
        ISessionActivationWindowRegistry windowRegistry)
    {
        _settings = settings;
        _codexClient = codexClient;
        _windowRegistry = windowRegistry;
        Reload();
    }

    public IReadOnlyList<ISignalSource> Current => _current;

    public void Reload()
    {
        var sources = new List<ISignalSource>();
        foreach (var account in _settings.GetEffectiveAccounts())
        {
            var displayName = MonitoredAccountSettings.NormalizeDisplayName(account.DisplayName, account.Id);
            try
            {
                if (account.IsCodex)
                {
                    sources.Add(new CodexAppServerSource(
                        new CodexAccountProfile(account.Id, displayName, account.ConfigDir),
                        _codexClient,
                        _windowRegistry));
                }
                else if (account.IsClaude)
                {
                    sources.Add(new ClaudeSubscriptionSource(
                        new ClaudeAccountProfile(account.Id, displayName, account.ConfigDir),
                        new ClaudeOAuthUsageFetcher(account.ConfigDir),
                        _windowRegistry));
                }
            }
            catch (ArgumentException)
            {
                // Invalid id/name from a hand-edited settings file: skip the account
                // instead of taking the whole registry down.
            }
        }

        _current = sources;
    }
}
