using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;

namespace costats.App.Services.Updates;

public sealed class StartupUpdateCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Regex SemVerRegex = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShaLineRegex = new(
        @"^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepositoryRegex = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly UpdateOptions _options;
    private readonly HttpClient _httpClient;
    private readonly string _appBaseDirectory;
    private readonly string _executablePath;
    private readonly string _updatesRoot;
    private readonly string _statePath;
    private readonly string _pendingPath;
    private readonly string _runtimeRid;
    private readonly Version _currentVersion;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public StartupUpdateCoordinator(UpdateOptions options)
    {
        _options = options;
        _appBaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _executablePath = Environment.ProcessPath ?? Path.Combine(_appBaseDirectory, "AIUsageTray.exe");
        _runtimeRid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        _currentVersion = ResolveCurrentVersion();

        _updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "costats",
            "updates");
        _statePath = Path.Combine(_updatesRoot, "state.json");
        _pendingPath = Path.Combine(_updatesRoot, "pending.json");

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("costats", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task<bool> TryApplyPendingUpdateAsync(CancellationToken cancellationToken)
        => TryApplyPendingUpdateAsync(cancellationToken, manualTrigger: false);

    public async Task<bool> TryApplyPendingUpdateAsync(CancellationToken cancellationToken, bool manualTrigger)
    {
        if (!_options.Enabled || !CanSelfUpdate())
        {
            return false;
        }

        // Only gate on ApplyStagedUpdateOnStartup for automatic (non-manual) triggers
        if (!manualTrigger && !_options.ApplyStagedUpdateOnStartup)
        {
            return false;
        }

        try
        {
            var pending = await ReadJsonAsync<PendingUpdate>(_pendingPath, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                return false;
            }

            const int maxApplyAttempts = 3;
            if (pending.FailedAttempts >= maxApplyAttempts)
            {
                Trace.WriteLine($"[costats-update] pending update {pending.Version} failed {pending.FailedAttempts} times, giving up");
                SafeDeleteFile(_pendingPath);
                SafeDeleteDirectory(pending.StagingDirectory);
                return false;
            }

            if (!TryParseSemVer(pending.Version, out var pendingVersion) || pendingVersion <= _currentVersion)
            {
                SafeDeleteFile(_pendingPath);
                SafeDeleteDirectory(pending.StagingDirectory);
                return false;
            }

            if (!TryResolvePendingExecutable(pending, out var stagedExe, out var executableRelativePath))
            {
                SafeDeleteFile(_pendingPath);
                return false;
            }

            Directory.CreateDirectory(_updatesRoot);
            var scriptPath = Path.Combine(_updatesRoot, "apply-update.ps1");

            // Prefer the script shipped with the staged update (from the new version's ZIP).
            // This prevents a chicken-and-egg problem where the running version's embedded
            // script has a bug that can only be fixed by the version being installed.
            var stagedScript = Path.Combine(pending.StagingDirectory, "apply-update.ps1");
            if (File.Exists(stagedScript))
            {
                File.Copy(stagedScript, scriptPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(scriptPath, UpdaterScriptContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                    .ConfigureAwait(false);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-TargetPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-InstallDir");
            psi.ArgumentList.Add(_appBaseDirectory);
            psi.ArgumentList.Add("-StagingDir");
            psi.ArgumentList.Add(pending.StagingDirectory);
            psi.ArgumentList.Add("-ExecutableRelativePath");
            psi.ArgumentList.Add(executableRelativePath);
            psi.ArgumentList.Add("-PendingFilePath");
            psi.ArgumentList.Add(_pendingPath);

            var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            Trace.WriteLine($"[costats-update] launching updater for version {pending.Version} from {stagedExe}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[costats-update] apply staged update failed: {ex}");
            return false;
        }
    }

    public async Task<UpdateCheckResult> CheckAndStageUpdateAsync(CancellationToken cancellationToken, bool forceCheck = false)
    {
        if (!_options.Enabled || !CanSelfUpdate())
        {
            return UpdateCheckResult.Disabled;
        }

        if (!await _checkLock.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
        {
            return UpdateCheckResult.AlreadyRunning;
        }

        try
        {
            Directory.CreateDirectory(_updatesRoot);
            var pending = await ReadJsonAsync<PendingUpdate>(_pendingPath, cancellationToken).ConfigureAwait(false);
            if (pending is not null && IsPendingValidAndNewer(pending))
            {
                return UpdateCheckResult.UpdateAlreadyStaged;
            }

            var state = await ReadJsonAsync<UpdateState>(_statePath, cancellationToken).ConfigureAwait(false) ?? new UpdateState();
            var now = DateTimeOffset.UtcNow;
            var interval = TimeSpan.FromHours(_options.CheckIntervalHours);
            if (!forceCheck && state.LastCheckedUtc.HasValue && now - state.LastCheckedUtc.Value < interval)
            {
                return UpdateCheckResult.Skipped;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildLatestReleaseUri(_options.Repository));
            if (!string.IsNullOrWhiteSpace(state.ETag) &&
                EntityTagHeaderValue.TryParse(state.ETag, out var eTagHeader))
            {
                request.Headers.IfNoneMatch.Add(eTagHeader);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            state.LastCheckedUtc = now;
            state.ETag = response.Headers.ETag?.Tag ?? state.ETag;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.CheckFailed;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await ParseReleaseAsync(contentStream, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.CheckFailed;
            }

            if (release.Prerelease && !_options.AllowPrerelease)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (!TryGetBestAsset(release, out var zipAsset, out var releaseVersion))
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (releaseVersion <= _currentVersion)
            {
                state.LastSeenVersion = releaseVersion.ToString(3);
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            // Persist the check timestamp before downloading so a repeatedly
            // failing release does not trigger a fresh download on every start.
            state.LastSeenVersion = releaseVersion.ToString(3);
            await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);

            var downloadsDir = Path.Combine(_updatesRoot, "downloads");
            Directory.CreateDirectory(downloadsDir);
            var zipPath = Path.Combine(downloadsDir, zipAsset.Name);
            await DownloadToFileAsync(zipAsset.DownloadUrl, zipPath, cancellationToken).ConfigureAwait(false);

            try
            {
                // The checksum is mandatory: an unverified ZIP is never extracted,
                // because the updater also runs the apply-update.ps1 it contains.
                var expectedHash = await TryResolveChecksumAsync(release, zipAsset, cancellationToken).ConfigureAwait(false);
                var actualHash = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
                EnsureChecksumMatches(expectedHash, actualHash, zipAsset.Name);
            }
            catch
            {
                SafeDeleteFile(zipPath);
                throw;
            }

            var stageDir = Path.Combine(
                _updatesRoot,
                "staging",
                $"{releaseVersion.Major}.{releaseVersion.Minor}.{releaseVersion.Build}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            if (Directory.Exists(stageDir))
            {
                Directory.Delete(stageDir, recursive: true);
            }

            Directory.CreateDirectory(stageDir);
            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);

            if (!TryFindStagedExecutable(stageDir, out var stagedExecutablePath))
            {
                throw new FileNotFoundException("Staged update did not contain AIUsageTray.exe.");
            }

            var executableRelativePath = Path.GetRelativePath(stageDir, stagedExecutablePath);
            var pendingUpdate = new PendingUpdate
            {
                Version = releaseVersion.ToString(3),
                CreatedUtc = DateTimeOffset.UtcNow,
                StagingDirectory = stageDir,
                ExecutableRelativePath = executableRelativePath
            };

            await WriteJsonAsync(_pendingPath, pendingUpdate, cancellationToken).ConfigureAwait(false);

            state.LastSeenVersion = releaseVersion.ToString(3);
            await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);

            SafeDeleteFile(zipPath);
            CleanupOldStagingDirectories(stageDir);

            return UpdateCheckResult.UpdateStaged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[costats-update] check/stage failed: {ex}");
            return UpdateCheckResult.CheckFailed;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private static string BuildLatestReleaseUri(string repository)
    {
        if (!IsValidRepositoryName(repository))
        {
            throw new InvalidOperationException($"Update repository '{repository}' is not a valid owner/name pair.");
        }

        var uri = $"https://api.github.com/repos/{repository}/releases/latest";
        EnsureTrustedUrl(uri);
        return uri;
    }

    /// <summary>Only "owner/name" is accepted, so the release URL cannot be redirected elsewhere.</summary>
    internal static bool IsValidRepositoryName(string? repository)
    {
        return !string.IsNullOrWhiteSpace(repository) && RepositoryRegex.IsMatch(repository);
    }

    /// <summary>
    /// Release metadata is JSON from the network, so every URL taken from it is
    /// checked before a request is made: HTTPS only, and only GitHub hosts.
    /// </summary>
    internal static bool IsTrustedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureTrustedUrl(string? url)
    {
        if (!IsTrustedUrl(url))
        {
            throw new InvalidOperationException($"Refusing to fetch update content from an untrusted URL: {url}");
        }
    }

    /// <summary>
    /// Fails when no published checksum was found as well as on a mismatch: a
    /// release without a usable .sha256 asset is treated as a failed check.
    /// </summary>
    internal static void EnsureChecksumMatches(string? expectedHash, string actualHash, string assetName)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            throw new InvalidDataException(
                $"No published SHA-256 checksum was found for {assetName}. Refusing to install an unverified download.");
        }

        if (!string.Equals(expectedHash.Trim(), actualHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Downloaded update {assetName} does not match the published SHA-256 checksum.");
        }
    }

    private bool CanSelfUpdate()
    {
        if (!File.Exists(_executablePath))
        {
            return false;
        }

        if (_appBaseDirectory.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            // MSIX/AppInstaller installs are updated by App Installer.
            return false;
        }

        if (InstallMarker.IsDevelopmentDirectory(_appBaseDirectory))
        {
            // Development runs should not self-update.
            return false;
        }

        if (!HasWriteAccess(_appBaseDirectory))
        {
            return false;
        }

        if (!InstallMarker.IsManagedInstallDirectory(_appBaseDirectory, installedBy: "self-update-migration"))
        {
            // The updater replaces the install directory as a whole. Without the
            // install marker there is no proof the folder belongs to this app,
            // and a ZIP extracted next to unrelated files would be wiped.
            Trace.WriteLine($"[costats-update] self-update disabled: {_appBaseDirectory} is not a managed install directory");
            return false;
        }

        return true;
    }

    private static bool HasWriteAccess(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testPath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testPath, "ok");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPendingValidAndNewer(PendingUpdate pending)
    {
        if (!TryResolvePendingExecutable(pending, out _, out _))
        {
            SafeDeleteFile(_pendingPath);
            return false;
        }

        return TryParseSemVer(pending.Version, out var pendingVersion) && pendingVersion > _currentVersion;
    }

    private static bool TryResolvePendingExecutable(PendingUpdate pending, out string stagedExePath, out string executableRelativePath)
    {
        stagedExePath = string.Empty;
        executableRelativePath = "AIUsageTray.exe";

        if (string.IsNullOrWhiteSpace(pending.StagingDirectory) || !Directory.Exists(pending.StagingDirectory))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pending.ExecutableRelativePath))
        {
            var candidate = Path.Combine(pending.StagingDirectory, pending.ExecutableRelativePath);
            if (File.Exists(candidate))
            {
                stagedExePath = candidate;
                executableRelativePath = pending.ExecutableRelativePath;
                return true;
            }
        }

        if (!TryFindStagedExecutable(pending.StagingDirectory, out var discoveredExecutable))
        {
            return false;
        }

        stagedExePath = discoveredExecutable;
        executableRelativePath = Path.GetRelativePath(pending.StagingDirectory, discoveredExecutable);
        return true;
    }

    private static bool TryFindStagedExecutable(string stageDirectory, out string executablePath)
    {
        executablePath = Path.Combine(stageDirectory, "AIUsageTray.exe");
        if (File.Exists(executablePath))
        {
            return true;
        }

        var discovered = Directory
            .EnumerateFiles(stageDirectory, "AIUsageTray.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(discovered))
        {
            executablePath = string.Empty;
            return false;
        }

        executablePath = discovered;
        return true;
    }

    private static void CleanupOldStagingDirectories(string keepPath)
    {
        var parent = Path.GetDirectoryName(keepPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(dir, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault();

        if (TryParseSemVer(informational, out var informationalVersion))
        {
            return informationalVersion;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null && assemblyVersion.Major >= 0 && assemblyVersion.Minor >= 0 && assemblyVersion.Build >= 0)
        {
            return new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
        }

        return new Version(0, 0, 0);
    }

    private bool TryGetBestAsset(ReleaseDocument release, out ReleaseAsset selectedAsset, out Version selectedVersion)
    {
        selectedAsset = default!;
        selectedVersion = new Version(0, 0, 0);

        var candidates = new List<(ReleaseAsset Asset, Version Version)>();
        foreach (var asset in release.Assets)
        {
            if (!TryExtractVersionFromAssetName(asset.Name, out var assetRid, out var parsedVersion))
            {
                continue;
            }

            candidates.Add((asset with { RuntimeIdentifier = assetRid }, parsedVersion));
        }

        var best = candidates
            .Where(candidate => string.Equals(candidate.Asset.RuntimeIdentifier, _runtimeRid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();

        if (best.Asset is null || string.IsNullOrWhiteSpace(best.Asset.Name))
        {
            return false;
        }

        selectedAsset = best.Asset;
        selectedVersion = best.Version;
        return true;
    }

    private static bool TryExtractVersionFromAssetName(string assetName, out string runtimeIdentifier, out Version version)
    {
        runtimeIdentifier = string.Empty;
        version = new Version(0, 0, 0);

        if (!assetName.StartsWith("ai-usage-tray-win-", StringComparison.OrdinalIgnoreCase) ||
            !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerIndex = assetName.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return false;
        }

        runtimeIdentifier = assetName["ai-usage-tray-".Length..markerIndex];
        var versionText = assetName[(markerIndex + 2)..^4];
        return TryParseSemVer(versionText, out version);
    }

    private static bool TryParseSemVer(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = SemVerRegex.Match(value.TrimStart('v', 'V').Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }

    private async Task<string?> TryResolveChecksumAsync(ReleaseDocument release, ReleaseAsset packageAsset, CancellationToken cancellationToken)
    {
        var directChecksumAsset = release.Assets
            .FirstOrDefault(asset => string.Equals(asset.Name, $"{packageAsset.Name}.sha256", StringComparison.OrdinalIgnoreCase));
        if (directChecksumAsset is not null && !string.IsNullOrWhiteSpace(directChecksumAsset.Name))
        {
            var checksumText = await DownloadAsStringAsync(directChecksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            return ExtractChecksum(checksumText, packageAsset.Name);
        }

        var checksumsAsset = release.Assets
            .FirstOrDefault(asset => string.Equals(asset.Name, "checksums.txt", StringComparison.OrdinalIgnoreCase));
        if (checksumsAsset is not null && !string.IsNullOrWhiteSpace(checksumsAsset.Name))
        {
            var checksumText = await DownloadAsStringAsync(checksumsAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            return ExtractChecksum(checksumText, packageAsset.Name);
        }

        return null;
    }

    internal static string? ExtractChecksum(string checksumText, string packageName)
    {
        if (string.IsNullOrWhiteSpace(checksumText))
        {
            return null;
        }

        foreach (var line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Regex.IsMatch(line, "^[A-Fa-f0-9]{64}$"))
            {
                return line.Trim();
            }

            var match = ShaLineRegex.Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var candidateName = match.Groups["name"].Value.Trim();
            if (string.Equals(candidateName, packageName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.Trim();
            }
        }

        return null;
    }

    private async Task<string> DownloadAsStringAsync(string url, CancellationToken cancellationToken)
    {
        EnsureTrustedUrl(url);
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        EnsureTrustedUrl(url);
        var tempPath = $"{destinationPath}.part";
        SafeDeleteFile(tempPath);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // HttpClient.Timeout only covers headers with ResponseHeadersRead.
        // Add an explicit timeout for the body download to prevent indefinite hangs.
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCts.CancelAfter(TimeSpan.FromMinutes(3));

        await using (var source = await response.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, downloadCts.Token).ConfigureAwait(false);
        }

        SafeDeleteFile(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<ReleaseDocument?> ParseReleaseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            var name = assetElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            assets.Add(new ReleaseAsset(name, downloadUrl));
        }

        var prerelease = root.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.GetBoolean();
        return new ReleaseDocument(prerelease, assets);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void SafeDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record ReleaseDocument(bool Prerelease, IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl, string RuntimeIdentifier = "");

    private sealed class UpdateState
    {
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public string? ETag { get; set; }
        public string? LastSeenVersion { get; set; }
    }

    private sealed class PendingUpdate
    {
        public string Version { get; set; } = "0.0.0";
        public DateTimeOffset CreatedUtc { get; set; }
        public string StagingDirectory { get; set; } = string.Empty;
        public string ExecutableRelativePath { get; set; } = "AIUsageTray.exe";
        public int FailedAttempts { get; set; }
    }

    private const string UpdaterScriptContents = """
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$StagingDir,
    [Parameter(Mandatory = $true)][string]$ExecutableRelativePath,
    [Parameter(Mandatory = $true)][string]$PendingFilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$logDir = Join-Path $env:LOCALAPPDATA "costats\updates"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir "apply-update.log"

# Track state for guaranteed relaunch
$updateSucceeded = $false
$backupDir = "$InstallDir.__backup"
$oldExePath = Join-Path $InstallDir $ExecutableRelativePath
$newExePath = $null

# The whole InstallDir is swapped below, so it must be a folder that belongs to
# this app and nothing else. install.ps1 writes this marker after extracting and
# every update carries it forward; without it the swap is refused.
$installMarkerName = "install-manifest.json"
$installMarkerApp = "AIUsageTray"
$installMarkerSchema = 1

function Write-Log {
    param([string]$Message)
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    Add-Content -Path $logPath -Value "[$stamp] $Message"
}

function Invoke-WithRetry {
    param(
        [scriptblock]$Action,
        [int]$Attempts = 20,
        [int]$DelayMs = 1500
    )

    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            & $Action
            return
        } catch {
            if ($i -ge $Attempts) {
                throw
            }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

function Test-InstallMarker {
    param([string]$Directory)

    if ([string]::IsNullOrWhiteSpace($Directory)) { return $false }

    $markerPath = Join-Path $Directory $installMarkerName
    if (-not (Test-Path -LiteralPath $markerPath)) { return $false }

    try {
        $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
        if (-not (Get-Member -InputObject $marker -Name "app" -MemberType NoteProperty)) { return $false }
        return ($marker.app -eq $installMarkerApp)
    } catch {
        return $false
    }
}

function New-InstallMarker {
    param([string]$Directory)

    try {
        $payload = [ordered]@{
            app           = $installMarkerApp
            schemaVersion = $installMarkerSchema
            installedUtc  = (Get-Date).ToUniversalTime().ToString("o")
            installedBy   = "apply-update.ps1"
        }
        $markerPath = Join-Path $Directory $installMarkerName
        $payload | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
        Write-Log "Wrote the install marker to $markerPath."
    } catch {
        Write-Log "Could not write the install marker: $($_.Exception.Message)"
    }
}

function Test-ForbiddenInstallDir {
    param([string]$Directory)

    $full = [IO.Path]::GetFullPath($Directory)
    $candidates = @([IO.Path]::GetPathRoot($full), $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA,
                    $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:windir)
    if ($env:USERPROFILE) {
        $candidates += (Join-Path $env:USERPROFILE "Desktop")
        $candidates += (Join-Path $env:USERPROFILE "Documents")
        $candidates += (Join-Path $env:USERPROFILE "Downloads")
    }

    $forbidden = $candidates | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('/', [char]92) }
    return ($forbidden -contains $full.TrimEnd('/', [char]92))
}

function Relaunch-App {
    # Try new exe first, fall back to old exe, fall back to any exe we can find
    $candidates = @()
    if ($newExePath -and (Test-Path $newExePath)) { $candidates += $newExePath }
    $currentExe = Join-Path $InstallDir $ExecutableRelativePath
    if ((Test-Path $currentExe) -and ($candidates -notcontains $currentExe)) { $candidates += $currentExe }
    $backupExe = Join-Path $backupDir $ExecutableRelativePath
    if ((Test-Path $backupExe) -and ($candidates -notcontains $backupExe)) { $candidates += $backupExe }
    $stagedExe = Join-Path $StagingDir $ExecutableRelativePath
    if ((Test-Path $stagedExe) -and ($candidates -notcontains $stagedExe)) { $candidates += $stagedExe }

    foreach ($exe in $candidates) {
        try {
            Start-Process -FilePath $exe | Out-Null
            Write-Log "Launched app: $exe"
            return
        } catch {
            Write-Log "Failed to launch $exe : $($_.Exception.Message)"
        }
    }
    Write-Log "CRITICAL: Could not launch any executable. Candidates: $($candidates -join ', ')"
}

function Increment-FailedAttempts {
    try {
        if (Test-Path $PendingFilePath) {
            $json = Get-Content -Raw -Path $PendingFilePath | ConvertFrom-Json
            if (-not (Get-Member -InputObject $json -Name "failedAttempts" -MemberType NoteProperty)) {
                $json | Add-Member -NotePropertyName "failedAttempts" -NotePropertyValue 0
            }
            $json.failedAttempts = $json.failedAttempts + 1
            $json | ConvertTo-Json -Depth 10 | Set-Content -Path $PendingFilePath -Encoding UTF8
            Write-Log "Incremented failedAttempts to $($json.failedAttempts)."
        }
    } catch {
        Write-Log "Failed to increment failedAttempts: $($_.Exception.Message)"
    }
}

Write-Log "Starting staged update."
Write-Log "InstallDir=$InstallDir"
Write-Log "StagingDir=$StagingDir"

# --- Circuit breaker: abort if too many failed attempts ---
$maxAttempts = 3
try {
    if (Test-Path $PendingFilePath) {
        $pendingJson = Get-Content -Raw -Path $PendingFilePath | ConvertFrom-Json
        $currentAttempts = 0
        if (Get-Member -InputObject $pendingJson -Name "failedAttempts" -MemberType NoteProperty) {
            $currentAttempts = $pendingJson.failedAttempts
        }
        if ($currentAttempts -ge $maxAttempts) {
            Write-Log "Update has failed $currentAttempts times (max $maxAttempts). Giving up and removing pending update."
            Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
            Relaunch-App
            return
        }
    }
} catch {
    Write-Log "Failed to read failedAttempts: $($_.Exception.Message)"
}

try {
    # --- Wait for target process to exit ---
    Write-Log "Waiting for process $TargetPid to exit..."
    for ($i = 0; $i -lt 120; $i++) {
        if (-not (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) {
            Write-Log "Process exited after $([math]::Round($i * 0.5, 1))s."
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) {
        Write-Log "Target process still running after 60s. Stopping forcefully."
        Stop-Process -Id $TargetPid -Force -ErrorAction SilentlyContinue
    }

    # Wait for Windows to fully release file handles after process death.
    # Antivirus, Windows Search indexer, and .NET single-file extraction cache
    # can hold handles for several seconds after the process is gone.
    Write-Log "Waiting for file handles to release..."
    Start-Sleep -Seconds 5

    # --- Validate staging ---
    if (-not (Test-Path $StagingDir)) {
        Write-Log "Staging directory not found: $StagingDir"
        Relaunch-App
        return
    }

    $stagedExeCheck = Join-Path $StagingDir $ExecutableRelativePath
    if (-not (Test-Path $stagedExeCheck)) {
        Write-Log "Staged executable not found: $stagedExeCheck"
        Relaunch-App
        return
    }

    # --- Validate the install directory ---
    # Everything below replaces InstallDir as a whole, so refuse anything that is
    # not provably a dedicated AI Usage Tray folder. The pending update is dropped
    # so the app does not retry the same refusal on every start.
    if (Test-ForbiddenInstallDir -Directory $InstallDir) {
        Write-Log "Refusing to update: '$InstallDir' is a drive root, profile or system folder."
        Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
        Relaunch-App
        return
    }

    if (-not (Test-InstallMarker -Directory $InstallDir)) {
        Write-Log "Refusing to update: '$InstallDir' has no valid $installMarkerName, so it is not a managed install."
        Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
        Relaunch-App
        return
    }

    # Carry the marker into the new version so the next update is allowed too.
    try {
        $currentMarkerPath = Join-Path $InstallDir $installMarkerName
        $stagedMarkerPath = Join-Path $StagingDir $installMarkerName
        Copy-Item -LiteralPath $currentMarkerPath -Destination $stagedMarkerPath -Force
        Write-Log "Carried the install marker into the staged version."
    } catch {
        Write-Log "Could not carry the install marker into staging: $($_.Exception.Message)"
    }

    # --- Clean old backup ---
    if (Test-Path $backupDir) {
        try {
            Invoke-WithRetry { Remove-Item -Recurse -Force $backupDir }
            Write-Log "Cleaned old backup directory."
        } catch {
            Write-Log "Could not clean old backup: $($_.Exception.Message)"
            # Non-fatal: try the swap anyway, old backup might not block it
        }
    }

    # --- Swap: move current install to backup ---
    try {
        Invoke-WithRetry { Move-Item -Path $InstallDir -Destination $backupDir }
        Write-Log "Moved install to backup."
    } catch {
        Write-Log "Cannot move install to backup: $($_.Exception.Message)"
        Write-Log "Update deferred to next startup. Relaunching current app."
        Increment-FailedAttempts
        Relaunch-App
        return
    }

    # --- Swap: move staging to install ---
    try {
        Invoke-WithRetry { Move-Item -Path $StagingDir -Destination $InstallDir }
        Write-Log "Moved staging to install."
    } catch {
        Write-Log "Cannot move staging to install: $($_.Exception.Message)"
        # Rollback: restore backup to install dir
        try {
            Move-Item -Path $backupDir -Destination $InstallDir -Force
            Write-Log "Rollback completed."
        } catch {
            Write-Log "CRITICAL: Rollback also failed: $($_.Exception.Message)"
        }
        Relaunch-App
        return
    }

    # --- Verify new executable ---
    $newExePath = Join-Path $InstallDir $ExecutableRelativePath
    if (-not (Test-Path $newExePath)) {
        Write-Log "New executable not found after swap: $newExePath. Rolling back."
        try {
            if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue }
            Move-Item -Path $backupDir -Destination $InstallDir -Force
            Write-Log "Rollback completed."
        } catch {
            Write-Log "Rollback failed: $($_.Exception.Message)"
        }
        Relaunch-App
        return
    }

    # The marker must survive the swap or the next update would refuse to run.
    if (-not (Test-InstallMarker -Directory $InstallDir)) {
        New-InstallMarker -Directory $InstallDir
    }

    $updateSucceeded = $true
    Write-Log "Swap completed successfully."

    # --- Cleanup ---
    if (Test-Path $PendingFilePath) {
        Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
    }

    if (Test-Path $backupDir) {
        try {
            Remove-Item -Recurse -Force $backupDir
        } catch {
            Write-Log "Backup cleanup failed (non-fatal): $($_.Exception.Message)"
        }
    }

    # --- Launch updated app ---
    Relaunch-App
    Write-Log "Update finished successfully."

} catch {
    Write-Log "Unexpected error: $($_.Exception.Message)"
    # Guarantee relaunch no matter what
    Relaunch-App
}
""";
}

/// <summary>
/// The install marker: a small JSON file written into the install directory that
/// proves the directory belongs to AI Usage Tray.
/// </summary>
/// <remarks>
/// The updater and the uninstaller both replace or delete the install directory
/// as a whole. The release ZIP is flat, so a user can extract it into a folder
/// that holds unrelated files, and without an ownership check those files would
/// be destroyed. install.ps1 writes the marker after extracting, apply-update.ps1
/// carries it into every new version, and uninstall.ps1 refuses to delete a
/// folder that does not have it.
///
/// This type lives next to <see cref="StartupUpdateCoordinator"/> so both the
/// updater and the Add/remove programs registration share one definition.
/// </remarks>
public static class InstallMarker
{
    /// <summary>File name of the marker, identical in the PowerShell scripts.</summary>
    public const string FileName = "install-manifest.json";

    /// <summary>Value of the "app" property that makes a marker ours.</summary>
    public const string AppIdentifier = "AIUsageTray";

    /// <summary>Current marker layout version.</summary>
    public const int SchemaVersion = 1;

    private const string ManagedRootFolderName = "AIUsageTray";
    private const string ManagedAppFolderName = "app";

    private static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Full path of the marker inside <paramref name="installDirectory"/>.</summary>
    public static string PathFor(string installDirectory) => Path.Combine(installDirectory, FileName);

    /// <summary>True when the text is a marker written for this app.</summary>
    public static bool IsValidContent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("app", out var appElement) ||
                appElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return string.Equals(appElement.GetString(), AppIdentifier, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when a valid marker sits in the directory.</summary>
    public static bool Exists(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return false;
        }

        try
        {
            var path = PathFor(installDirectory);
            return File.Exists(path) && IsValidContent(File.ReadAllText(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The marker payload. Kept in sync with the scripts by hand.</summary>
    public static string CreateContent(string installedBy, DateTimeOffset createdUtc)
    {
        return JsonSerializer.Serialize(
            new
            {
                app = AppIdentifier,
                schemaVersion = SchemaVersion,
                installedUtc = createdUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                installedBy
            },
            MarkerJsonOptions);
    }

    /// <summary>Writes a marker. Returns false instead of throwing.</summary>
    public static bool TryWrite(string installDirectory, string installedBy)
    {
        try
        {
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(
                PathFor(installDirectory),
                CreateContent(installedBy, DateTimeOffset.UtcNow),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Where install.ps1 puts the app when no directory is given.</summary>
    public static string DefaultManagedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ManagedRootFolderName,
        ManagedAppFolderName);

    /// <summary>True for %LOCALAPPDATA%\AIUsageTray\app.</summary>
    public static bool IsDefaultManagedDirectory(string? directory)
        => IsDefaultManagedDirectory(
            directory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Overload that takes the local app data root, so it can be tested.</summary>
    public static bool IsDefaultManagedDirectory(string? directory, string? localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(localApplicationData))
        {
            return false;
        }

        try
        {
            var expected = Path.Combine(localApplicationData, ManagedRootFolderName, ManagedAppFolderName);
            return string.Equals(Normalize(directory), Normalize(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True for a build running straight out of the repository.</summary>
    public static bool IsDevelopmentDirectory(string? directory)
    {
        return !string.IsNullOrWhiteSpace(directory) &&
               directory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
               directory.Contains(@"\src\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the directory may be replaced or removed as a whole. Installs
    /// made before the marker existed are migrated in place, but only at the
    /// default managed path, which install.ps1 has always owned exclusively.
    /// </summary>
    public static bool IsManagedInstallDirectory(string? directory, string installedBy)
    {
        if (string.IsNullOrWhiteSpace(directory) || IsDevelopmentDirectory(directory))
        {
            return false;
        }

        if (Exists(directory))
        {
            return true;
        }

        return IsDefaultManagedDirectory(directory) && TryWrite(directory, installedBy);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
