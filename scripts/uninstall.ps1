<#
.SYNOPSIS
    Uninstaller for AI Usage Tray (per-user, no elevation).

.DESCRIPTION
    Stops the app, removes the Start Menu and startup entries, deletes the
    Add/remove programs registration and deletes the installed files under
    %LOCALAPPDATA%\AIUsageTray.

    Your settings, logs and snapshots in %LOCALAPPDATA%\costats are kept unless
    you pass -PurgeData.

    This script ships inside the app folder it deletes, so it copies itself to
    %TEMP% and re-runs from there.

.PARAMETER PurgeData
    Also delete %LOCALAPPDATA%\costats (settings, logs, snapshots).

.PARAMETER Silent
    Do not print progress. Used by the Add/remove programs quiet uninstall.

.PARAMETER AppDir
    The installed app folder. Defaults to the folder this script sits in.

.PARAMETER Relaunched
    Internal: set on the copy running from %TEMP%. Do not pass this yourself.

.EXAMPLE
    .\uninstall.ps1

.EXAMPLE
    .\uninstall.ps1 -PurgeData
#>

param(
    [switch]$PurgeData,
    [switch]$Silent,
    [string]$AppDir = "",
    [switch]$Relaunched
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$processName = "AIUsageTray"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsageTray"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runValueNames = @("AiUsageTray", "AIUsageTray", "costats")
$shortcutName = "AI Usage Tray.lnk"
$logPath = Join-Path $env:TEMP "ai-usage-tray-uninstall.log"

function Write-Step {
    param([string]$Message, [string]$Colour = "Gray")
    $stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    try { Add-Content -LiteralPath $logPath -Value "$stamp  $Message" } catch { }
    if (-not $Silent) { Write-Host $Message -ForegroundColor $Colour }
}

function Remove-Tree {
    param([string]$Path)
    # Windows can hold the executable open for a moment after the process
    # exits, so give the delete a few tries before giving up.
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $Path)) { return $true }
            Remove-Item -LiteralPath $Path -Recurse -Force
            return $true
        } catch {
            Start-Sleep -Milliseconds 600
        }
    }
    return (-not (Test-Path -LiteralPath $Path))
}

if ([string]::IsNullOrWhiteSpace($AppDir)) {
    $AppDir = Split-Path -Parent $PSCommandPath
}
$AppDir = [IO.Path]::GetFullPath($AppDir)

# Never let a bad argument turn this into a profile wipe.
$forbidden = @([IO.Path]::GetPathRoot($AppDir), $env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:windir) |
    Where-Object { $_ } | ForEach-Object { $_.TrimEnd('/', [char]92) }
if ($forbidden -contains $AppDir.TrimEnd('/', [char]92)) {
    throw "Refusing to delete '$AppDir' - that is not an app folder."
}

if (-not $Relaunched) {
    # Step out of the folder we are about to delete.
    $stagedScript = Join-Path $env:TEMP ("ai-usage-tray-uninstall-" + [guid]::NewGuid().ToString("N") + ".ps1")
    Copy-Item -LiteralPath $PSCommandPath -Destination $stagedScript -Force

    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $stagedScript + '"'),
                   "-Relaunched", "-AppDir", ('"' + $AppDir + '"'))
    if ($PurgeData) { $arguments += "-PurgeData" }
    if ($Silent) { $arguments += "-Silent" }

    Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden
    exit 0
}

Write-Step "Uninstalling AI Usage Tray from $AppDir" "Cyan"

# 1. Stop the running app (it locks its own executable).
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step "Stopping $processName ($($running.Count) process(es))..."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 900
}

# 2. Start Menu shortcut.
$startMenuShortcut = Join-Path $env:APPDATA (Join-Path "Microsoft\Windows\Start Menu\Programs" $shortcutName)
if (Test-Path -LiteralPath $startMenuShortcut) {
    Remove-Item -LiteralPath $startMenuShortcut -Force -ErrorAction SilentlyContinue
    Write-Step "Removed the Start Menu shortcut."
}

# 3. Start-at-login entries (both the Run value and the Startup shortcut).
if (Test-Path -LiteralPath $runKey) {
    foreach ($valueName in $runValueNames) {
        try {
            $existing = Get-ItemProperty -LiteralPath $runKey -Name $valueName -ErrorAction SilentlyContinue
            if ($existing) {
                Remove-ItemProperty -LiteralPath $runKey -Name $valueName -Force -ErrorAction SilentlyContinue
                Write-Step "Removed the start-at-login entry '$valueName'."
            }
        } catch { }
    }
}
$startupShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) $shortcutName
if (Test-Path -LiteralPath $startupShortcut) {
    Remove-Item -LiteralPath $startupShortcut -Force -ErrorAction SilentlyContinue
    Write-Step "Removed the startup-folder shortcut."
}

# 4. Add/remove programs registration.
if (Test-Path -LiteralPath $uninstallKey) {
    Remove-Item -LiteralPath $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Step "Removed the Add/remove programs entry."
}

# 5. Installed files, then the install root if this was a standard install.
if (-not (Remove-Tree -Path $AppDir)) {
    Write-Step "Could not delete $AppDir - close AI Usage Tray and delete it manually." "Yellow"
} else {
    Write-Step "Deleted $AppDir"
}

# The standard layout is <root>\AIUsageTray\app, and the updater stages into
# the same root, so take the whole root with it. Matching on the folder name
# rather than on a LOCALAPPDATA prefix avoids short-path ("SHLOMI~1") mismatches.
$installRoot = Split-Path -Parent $AppDir
if ($installRoot -and (Split-Path -Leaf $installRoot) -eq "AIUsageTray" -and (Test-Path -LiteralPath $installRoot)) {
    if (Remove-Tree -Path $installRoot) {
        Write-Step "Deleted $installRoot"
    }
}

# 6. User data is kept by default: reinstalling should not lose the accounts.
$dataDir = Join-Path $env:LOCALAPPDATA "costats"
if ($PurgeData) {
    if (Remove-Tree -Path $dataDir) {
        Write-Step "Deleted settings and logs in $dataDir"
    }
} elseif (Test-Path -LiteralPath $dataDir) {
    Write-Step "Kept your settings in $dataDir (re-run with -PurgeData to remove them)."
}

Write-Step "AI Usage Tray has been uninstalled." "Green"

# The staged copy deletes itself; PowerShell does not keep the file open.
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
