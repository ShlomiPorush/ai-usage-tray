using System.Net;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using costats.App.Services.Updates;
using Xunit;

namespace costats.Core.Tests.Updates;

public sealed class UpdateCheckFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ai-usage-tray-update-check-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckForUpdate_DoesNotDownloadOrStageReleaseAssets()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "AIUsageTray.exe");
        File.WriteAllText(executablePath, "test executable");
        InstallMarker.TryWrite(_directory, "unit-test");

        const string releaseJson = """
        {
          "tag_name": "v99.0.0",
          "html_url": "https://github.com/ShlomiPorush/ai-usage-tray/releases/tag/v99.0.0",
          "body": "Important changes",
          "prerelease": false,
          "assets": [
            {
              "name": "ai-usage-tray-win-x64-v99.0.0.zip",
              "browser_download_url": "https://github.com/ShlomiPorush/ai-usage-tray/releases/download/v99.0.0/ai-usage-tray-win-x64-v99.0.0.zip"
            },
            {
              "name": "ai-usage-tray-win-x64-v99.0.0.zip.sha256",
              "browser_download_url": "https://github.com/ShlomiPorush/ai-usage-tray/releases/download/v99.0.0/ai-usage-tray-win-x64-v99.0.0.zip.sha256"
            }
          ]
        }
        """;

        var handler = new RecordingHandler(releaseJson);
        var coordinator = new StartupUpdateCoordinator(new UpdateOptions());
        SetField(coordinator, "_httpClient", new HttpClient(handler));
        SetField(coordinator, "_appBaseDirectory", _directory);
        SetField(coordinator, "_executablePath", executablePath);
        SetField(coordinator, "_updatesRoot", Path.Combine(_directory, "updates"));
        SetField(coordinator, "_statePath", Path.Combine(_directory, "updates", "state.json"));
        SetField(coordinator, "_pendingPath", Path.Combine(_directory, "updates", "pending.json"));
        SetField(coordinator, "_runtimeRid", "win-x64");
        SetField(coordinator, "_currentVersion", new Version(1, 0, 0));

        var result = await coordinator.CheckForUpdateAsync(CancellationToken.None, forceCheck: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Update);
        Assert.Equal("99.0.0", result.Update.Version);
        Assert.Equal("Important changes", result.Update.ReleaseNotes);
        Assert.EndsWith(".zip.sha256", result.Update.ChecksumDownloadUrl);
        Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", handler.Requests[0].Host);
        Assert.False(Directory.Exists(Path.Combine(_directory, "updates", "downloads")));
        Assert.False(File.Exists(Path.Combine(_directory, "updates", "pending.json")));

        var cachedResult = await coordinator.CheckForUpdateAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, cachedResult.Status);
        Assert.True(cachedResult.FromCache);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadAndStageUpdate_ReportsProgressAndCreatesPendingUpdate()
    {
        Directory.CreateDirectory(_directory);
        var executablePath = Path.Combine(_directory, "AIUsageTray.exe");
        File.WriteAllText(executablePath, "test executable");
        InstallMarker.TryWrite(_directory, "unit-test");

        var package = CreatePackage();
        var packageHash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var handler = new PackageHandler(package, packageHash);
        var coordinator = new StartupUpdateCoordinator(new UpdateOptions());
        SetField(coordinator, "_httpClient", new HttpClient(handler));
        SetField(coordinator, "_appBaseDirectory", _directory);
        SetField(coordinator, "_executablePath", executablePath);
        SetField(coordinator, "_updatesRoot", Path.Combine(_directory, "updates"));
        SetField(coordinator, "_statePath", Path.Combine(_directory, "updates", "state.json"));
        SetField(coordinator, "_pendingPath", Path.Combine(_directory, "updates", "pending.json"));
        SetField(coordinator, "_runtimeRid", "win-x64");
        SetField(coordinator, "_currentVersion", new Version(1, 0, 0));

        var update = new AvailableUpdate(
            "99.0.0",
            "Important changes",
            "https://github.com/ShlomiPorush/ai-usage-tray/releases/tag/v99.0.0",
            "ai-usage-tray-win-x64-v99.0.0.zip",
            "https://github.com/ShlomiPorush/ai-usage-tray/releases/download/v99.0.0/ai-usage-tray-win-x64-v99.0.0.zip",
            "https://github.com/ShlomiPorush/ai-usage-tray/releases/download/v99.0.0/ai-usage-tray-win-x64-v99.0.0.zip.sha256");
        var progress = new RecordingProgress();

        var staged = await coordinator.DownloadAndStageUpdateAsync(update, progress, CancellationToken.None);

        Assert.True(staged);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(progress.Values, value => value is { Stage: UpdateProgressStage.Downloading, Percentage: 100 });
        Assert.Contains(progress.Values, value => value.Stage == UpdateProgressStage.Verifying);
        Assert.Contains(progress.Values, value => value.Stage == UpdateProgressStage.Preparing);
        Assert.Contains(progress.Values, value => value.Stage == UpdateProgressStage.ReadyToInstall);

        var pendingPath = Path.Combine(_directory, "updates", "pending.json");
        Assert.True(File.Exists(pendingPath));
        using var pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
        Assert.Equal("99.0.0", pending.RootElement.GetProperty("version").GetString());
        var stagingDirectory = pending.RootElement.GetProperty("stagingDirectory").GetString();
        Assert.NotNull(stagingDirectory);
        Assert.True(File.Exists(Path.Combine(stagingDirectory, "AIUsageTray.exe")));
    }

    [Fact]
    public void FormatReleaseNotes_MakesGitHubMarkdownReadable()
    {
        const string markdown = """
        ## What's Changed
        * **Account email privacy** by [Shlomi](https://github.com/Shlomi)

        **Full Changelog**: https://github.com/example/compare/v1...v2
        """;

        var formatted = StartupUpdateCoordinator.FormatReleaseNotes(markdown);

        Assert.Equal("What's Changed\n• Account email privacy by Shlomi", formatted);
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

    private static void SetField<T>(StartupUpdateCoordinator coordinator, string name, T value)
    {
        var field = typeof(StartupUpdateCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(coordinator, value);
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var executable = archive.CreateEntry("AIUsageTray.exe");
            using (var writer = new StreamWriter(executable.Open(), Encoding.UTF8, leaveOpen: false))
            {
                writer.Write("new executable");
            }

            var script = archive.CreateEntry("apply-update.ps1");
            using var scriptWriter = new StreamWriter(script.Open(), Encoding.UTF8, leaveOpen: false);
            scriptWriter.Write("Write-Output 'apply'");
        }

        return stream.ToArray();
    }

    private sealed class RecordingHandler(string releaseJson) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class PackageHandler(byte[] package, string hash) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                ? new StringContent($"{hash}  ai-usage-tray-win-x64-v99.0.0.zip\n", Encoding.UTF8, "text/plain")
                : new ByteArrayContent(package);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class RecordingProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Values { get; } = [];

        public void Report(UpdateProgress value) => Values.Add(value);
    }
}
