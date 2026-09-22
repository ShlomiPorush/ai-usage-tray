using System.Reflection;
using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

public sealed class UpdaterScriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ai-usage-tray-updater-script-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Embedded_updater_script_matches_the_shipped_file()
    {
        // The coordinator writes the embedded copy to disk only when a staged
        // ZIP has no script of its own, so the two must stay identical or a
        // fallback update would run stale logic. This guards against drift.
        var shipped = File.ReadAllText(LocateShippedScript());
        Assert.Equal(Normalize(shipped), Normalize(StartupUpdateCoordinator.UpdaterScriptContents));
    }

    [Fact]
    public void Both_updater_copies_carry_the_loop_guards()
    {
        foreach (var script in new[] { StartupUpdateCoordinator.UpdaterScriptContents, File.ReadAllText(LocateShippedScript()) })
        {
            Assert.Contains("COSTATS_UPDATE_DEFERRED", script, StringComparison.Ordinal);
            Assert.Contains("[System.IO.FileShare]::None", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_deferred_relaunch_does_not_re_apply_the_pending_update()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "AIUsageTray.exe");
        File.WriteAllText(executablePath, "test executable");
        InstallMarker.TryWrite(_directory, "unit-test");

        var updatesRoot = Path.Combine(_directory, "updates");
        var stagingDirectory = Path.Combine(updatesRoot, "staging", "99.0.0");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "AIUsageTray.exe"), "new executable");
        var pendingPath = Path.Combine(updatesRoot, "pending.json");
        Directory.CreateDirectory(updatesRoot);
        File.WriteAllText(pendingPath, $$"""
        {
          "version": "99.0.0",
          "stagingDirectory": {{System.Text.Json.JsonSerializer.Serialize(stagingDirectory)}},
          "executableRelativePath": "AIUsageTray.exe",
          "failedAttempts": 0
        }
        """);

        var coordinator = new StartupUpdateCoordinator(new UpdateOptions());
        SetField(coordinator, "_appBaseDirectory", _directory);
        SetField(coordinator, "_executablePath", executablePath);
        SetField(coordinator, "_updatesRoot", updatesRoot);
        SetField(coordinator, "_statePath", Path.Combine(updatesRoot, "state.json"));
        SetField(coordinator, "_pendingPath", pendingPath);
        SetField(coordinator, "_runtimeRid", "win-x64");
        SetField(coordinator, "_currentVersion", new Version(1, 0, 0));

        Environment.SetEnvironmentVariable(StartupUpdateCoordinator.DeferredRelaunchVariable, "1");
        try
        {
            var applied = await coordinator.TryApplyPendingUpdateAsync(CancellationToken.None, manualTrigger: false);

            Assert.False(applied);
            // The pending update is left untouched so a later clean start can apply it.
            Assert.True(File.Exists(pendingPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StartupUpdateCoordinator.DeferredRelaunchVariable, null);
        }
    }

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');

    private static string LocateShippedScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "costats.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName, "src", "costats.App", "Services", "Updates", "apply-update.ps1");
        Assert.True(File.Exists(path), $"Shipped updater script not found at {path}");
        return path;
    }

    private static void SetField<T>(StartupUpdateCoordinator coordinator, string name, T value)
    {
        var field = typeof(StartupUpdateCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(coordinator, value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
