namespace costats.Application.Pulse;

/// <summary>Where the startup usage cache lives. Each call is one whole-file operation.</summary>
public interface IUsageSnapshotStore
{
    /// <summary>The stored text, or null when there is none or it cannot be read.</summary>
    string? Read();

    /// <summary>Replaces the stored text as a whole; a reader never sees half a file.</summary>
    Task WriteAsync(string text);

    Task DeleteAsync();
}
