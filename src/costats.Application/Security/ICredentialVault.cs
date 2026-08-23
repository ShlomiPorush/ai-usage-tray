namespace costats.Application.Security;

public interface ICredentialVault
{
    Task SaveAsync(string key, string secret, CancellationToken cancellationToken);

    Task<string?> LoadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the stored secret. Deleting a key that was never stored is a
    /// no-op, so callers can clear unconditionally.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
