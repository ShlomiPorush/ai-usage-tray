using System.Net;
using AdysTech.CredentialManager;
using costats.Application.Security;

namespace costats.Infrastructure.Security;

public sealed class CredentialVault : ICredentialVault
{
    private const string TargetPrefix = "costats:";
    private const string Username = "costats";

    public Task SaveAsync(string key, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = BuildTarget(key);
        var credential = new NetworkCredential(Username, secret);
        CredentialManager.SaveCredentials(target, credential, CredentialType.Generic);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = BuildTarget(key);
        var credential = CredentialManager.GetCredentials(target);
        return Task.FromResult(credential?.Password);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = BuildTarget(key);
        try
        {
            CredentialManager.RemoveCredentials(target, CredentialType.Generic);
        }
        catch
        {
            // Credential Manager throws when the target does not exist. Clearing
            // a secret that was never stored is a no-op, not an error.
        }

        return Task.CompletedTask;
    }

    private static string BuildTarget(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.", nameof(key));
        }

        return $"{TargetPrefix}{key.Trim()}";
    }
}
