namespace costats.App.Services.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Skipped,
    Disabled,
    AlreadyRunning,
    CheckFailed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    AvailableUpdate? Update = null,
    bool FromCache = false);

public sealed record AvailableUpdate(
    string Version,
    string ReleaseNotes,
    string ReleasePageUrl,
    string PackageName,
    string PackageDownloadUrl,
    string? ChecksumDownloadUrl);

public enum UpdateProgressStage
{
    Downloading,
    Verifying,
    Preparing,
    ReadyToInstall
}

public sealed record UpdateProgress(UpdateProgressStage Stage, int? Percentage = null);
