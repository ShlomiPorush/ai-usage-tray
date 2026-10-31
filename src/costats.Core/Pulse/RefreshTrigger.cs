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
    Silent,

    /// <summary>
    /// The usage stored by the previous run, shown at startup while the first
    /// refresh runs. Display only: never the current state, never uploaded,
    /// never an alert observation, and ignored by a view that already shows a
    /// live state.
    /// </summary>
    Cached
}
