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
