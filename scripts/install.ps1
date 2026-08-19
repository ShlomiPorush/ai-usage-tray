<#
.SYNOPSIS
    One-step installer for AI Usage Tray (fork of costats) (per-user).

.DESCRIPTION
    Downloads the latest release ZIP for your architecture, extracts it to
    %LOCALAPPDATA%\AIUsageTray\app, and creates a Start Menu shortcut.

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

function Get-ArchRid {
    # $env:PROCESSOR_ARCHITECTURE works on both Windows PowerShell 5.1 and PowerShell 7+
    # (RuntimeInformation.ProcessArchitecture is unavailable under 5.1 with StrictMode).
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { return "win-arm64" }
    return "win-x64"
}

function Get-LatestAssetUrl {
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

    return $asset.browser_download_url
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
$forbidden = @([IO.Path]::GetPathRoot($InstallDir), $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:windir) |
    Where-Object { $_ } | ForEach-Object { $_.TrimEnd('/', [char]92) }
if ($forbidden -contains $InstallDir.TrimEnd('/', [char]92)) {
    throw "Refusing to install into '$InstallDir' - choose a dedicated subfolder."
}

Write-Host "Installing AI Usage Tray..." -ForegroundColor Cyan
Write-Host "Install directory: $InstallDir" -ForegroundColor Gray

$downloadUrl = Get-LatestAssetUrl
$tempZip = Join-Path $env:TEMP "ai-usage-tray-latest.zip"

Write-Host "Downloading latest release..." -ForegroundColor Yellow
Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZip

if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Write-Host "Extracting..." -ForegroundColor Yellow
Expand-Archive -Path $tempZip -DestinationPath $InstallDir -Force

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
