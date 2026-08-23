<#
.SYNOPSIS
    One-step installer for AI Usage Tray (fork of costats) (per-user).

.DESCRIPTION
    Downloads the latest release ZIP for your architecture, verifies it against
    the published SHA-256 checksum, extracts it to %LOCALAPPDATA%\AIUsageTray\app,
    writes an install-manifest.json marker and creates a Start Menu shortcut.

    The install directory is wiped before extracting, so it must be empty or hold
    an existing AI Usage Tray installation.

.PARAMETER InstallDir
    Custom installation directory (defaults to %LOCALAPPDATA%\AIUsageTray\app).

.PARAMETER SkipShortcut
    Skip creating the Start Menu shortcut.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -InstallDir "D:\Apps\AIUsageTray" -SkipShortcut
#>

param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "AIUsageTray\app"),
    [switch]$SkipShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 may default to TLS 1.0/1.1, which GitHub rejects.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repo = "ShlomiPorush/ai-usage-tray"
$apiUrl = "https://api.github.com/repos/$repo/releases/latest"

# Written into the install directory so the updater and the uninstaller can tell
# a folder that belongs to this app from one that merely contains it.
$installMarkerName = "install-manifest.json"
$installMarkerApp = "AIUsageTray"
$installMarkerSchema = 1

function Get-ArchRid {
    # $env:PROCESSOR_ARCHITECTURE works on both Windows PowerShell 5.1 and PowerShell 7+
    # (RuntimeInformation.ProcessArchitecture is unavailable under 5.1 with StrictMode).
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { return "win-arm64" }
    return "win-x64"
}

function Assert-TrustedUrl {
    # Release metadata comes off the network, so only GitHub over HTTPS is used.
    param([string]$Url)

    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        throw "Refusing to download from an unusable URL: $Url"
    }
    if ($uri.Scheme -ne "https") {
        throw "Refusing to download over $($uri.Scheme): $Url"
    }

    # Note: $host is a reserved automatic variable, so the name is spelled out.
    $targetHost = $uri.Host
    $allowed = ($targetHost -eq "github.com") -or ($targetHost -eq "api.github.com") -or
               ($targetHost -eq "githubusercontent.com") -or ($targetHost.EndsWith(".githubusercontent.com"))
    if (-not $allowed) {
        throw "Refusing to download from an untrusted host: $targetHost"
    }
}

function Get-LatestPackage {
    $headers = @{ "User-Agent" = "ai-usage-tray-installer" }
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
    if (-not $release.assets) {
        throw "No release assets found."
    }

    $rid = Get-ArchRid
    $pattern = "ai-usage-tray-$rid-v"
    $asset = $release.assets | Where-Object { $_.name -like "$pattern*.zip" } | Select-Object -First 1
    if (-not $asset) {
        throw "No release asset found for $rid."
    }

    $checksumName = "$($asset.name).sha256"
    $checksum = $release.assets | Where-Object { $_.name -eq $checksumName } | Select-Object -First 1
    if (-not $checksum) {
        throw "Release asset $($asset.name) has no $checksumName checksum. Refusing to install an unverified download."
    }

    Assert-TrustedUrl -Url $asset.browser_download_url
    Assert-TrustedUrl -Url $checksum.browser_download_url

    return [PSCustomObject]@{
        Name        = $asset.name
        Url         = $asset.browser_download_url
        ChecksumUrl = $checksum.browser_download_url
    }
}

function Assert-Checksum {
    # The published file is "<sha256>  <zip name>"; a bare hash is accepted too.
    param([string]$ZipPath, [string]$ChecksumUrl, [string]$AssetName)

    $headers = @{ "User-Agent" = "ai-usage-tray-installer" }
    $checksumText = (Invoke-WebRequest -Uri $ChecksumUrl -Headers $headers -UseBasicParsing).Content
    $expected = $null
    foreach ($line in ($checksumText -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^([A-Fa-f0-9]{64})(\s+\*?(.+))?$') {
            $name = $matches[3]
            if (-not $name -or $name.Trim() -ieq $AssetName) {
                $expected = $matches[1]
                break
            }
        }
    }

    if (-not $expected) {
        throw "Could not read a SHA-256 checksum for $AssetName. Refusing to install an unverified download."
    }

    $actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    if ($actual -ine $expected) {
        Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
        throw "Checksum mismatch for $AssetName (expected $expected, got $actual). The download was deleted."
    }
}

function New-InstallMarker {
    param([string]$Directory)

    $payload = [ordered]@{
        app           = $installMarkerApp
        schemaVersion = $installMarkerSchema
        installedUtc  = (Get-Date).ToUniversalTime().ToString("o")
        installedBy   = "install.ps1"
    }
    $payload | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory $installMarkerName) -Encoding UTF8
}

function Find-Executable {
    param([string]$Root)
    $candidates = Get-ChildItem -Path $Root -Filter "*.exe" -File -Recurse
    if (-not $candidates) { return $null }

    $preferred = $candidates | Where-Object { $_.Name -ieq "AIUsageTray.exe" } | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }

    return ($candidates | Sort-Object Length -Descending | Select-Object -First 1).FullName
}

function New-StartMenuShortcut {
    param([string]$TargetPath)
    $startMenu = Join-Path $env:APPDATA "Microsoft\\Windows\\Start Menu\\Programs"
    $shortcutPath = Join-Path $startMenu "AI Usage Tray.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = Split-Path $TargetPath
    $shortcut.Save()
}

# Refuse dangerous install targets before anything is removed: the directory is
# wiped during install, so an empty value or a drive/user-profile root would be
# catastrophic.
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    throw "InstallDir is empty."
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$candidates = @([IO.Path]::GetPathRoot($InstallDir), $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA,
                $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:windir)
if ($env:USERPROFILE) {
    $candidates += (Join-Path $env:USERPROFILE "Desktop")
    $candidates += (Join-Path $env:USERPROFILE "Documents")
    $candidates += (Join-Path $env:USERPROFILE "Downloads")
}
$forbidden = $candidates | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('/', [char]92) }
if ($forbidden -contains $InstallDir.TrimEnd('/', [char]92)) {
    throw "Refusing to install into '$InstallDir' - choose a dedicated subfolder."
}

# The directory is wiped below, so it must be empty or an existing install of
# this app. Anything else is someone else's data.
if (Test-Path -LiteralPath $InstallDir) {
    $existing = @(Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        $hasMarker = Test-Path -LiteralPath (Join-Path $InstallDir $installMarkerName)
        $hasExe = Test-Path -LiteralPath (Join-Path $InstallDir "AIUsageTray.exe")
        if (-not ($hasMarker -or $hasExe)) {
            throw "'$InstallDir' is not empty and does not contain an AI Usage Tray installation. Installing would delete its contents - choose an empty or dedicated directory."
        }
    }
}

Write-Host "Installing AI Usage Tray..." -ForegroundColor Cyan
Write-Host "Install directory: $InstallDir" -ForegroundColor Gray

$package = Get-LatestPackage
$tempZip = Join-Path $env:TEMP "ai-usage-tray-latest.zip"

Write-Host "Downloading latest release..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $package.Url -OutFile $tempZip

Write-Host "Verifying checksum..." -ForegroundColor Yellow
Assert-Checksum -ZipPath $tempZip -ChecksumUrl $package.ChecksumUrl -AssetName $package.Name

if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -Recurse -Force -LiteralPath $InstallDir
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Write-Host "Extracting..." -ForegroundColor Yellow
Expand-Archive -Path $tempZip -DestinationPath $InstallDir -Force

New-InstallMarker -Directory $InstallDir

$exePath = Find-Executable -Root $InstallDir
if (-not $exePath) {
    throw "Unable to find AI Usage Tray executable."
}

if (-not $SkipShortcut) {
    New-StartMenuShortcut -TargetPath $exePath
    Write-Host "Start Menu shortcut created." -ForegroundColor Green
}

Write-Host "Done. Launching AI Usage Tray..." -ForegroundColor Green
Start-Process -FilePath $exePath
