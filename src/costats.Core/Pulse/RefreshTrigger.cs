namespace costats.Core.Pulse;

public enum RefreshTrigger
{
    /// <summary>First load on app startup.</summary>
    Initial,

    /// <summary>User-initiated refresh (shows loading indicator).</summary>
    Manual,

    /// <summary>Background periodic refresh.</summary>
    Scheduled,

    /// <summary>Silent refresh when panel opens (no loading indicator).</summary>
    Silent
}
