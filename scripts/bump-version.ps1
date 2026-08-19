<#
.SYNOPSIS
    Bumps the version number across all AI Usage Tray projects.

.DESCRIPTION
    Updates version in:
      - src/Directory.Build.props  (the .NET single source of truth)
      - tools/insights-cli/package.json  (npm package, optional)
      - tools/insights-cli/package-lock.json (npm lockfile, optional)

.PARAMETER Version
    Explicit version in major.minor.patch format (e.g. 1.2.3).
    Cannot be combined with -Bump.

.PARAMETER Bump
    Semantic bump type: major, minor, or patch.
    Reads the current version from Directory.Build.props and increments accordingly.
    Cannot be combined with -Version.

.PARAMETER IncludeCli
    Also bump the insights-cli npm package to the same version. Default: false.

.PARAMETER DryRun
    Show what would change without modifying any files.

.EXAMPLE
    .\bump-version.ps1 -Version "1.2.0"
    Sets all versions to 1.2.0.

.EXAMPLE
    .\bump-version.ps1 -Bump patch
    Increments the patch version (e.g. 1.1.0 -> 1.1.1).

.EXAMPLE
    .\bump-version.ps1 -Bump minor -IncludeCli
    Increments the minor version and also updates insights-cli.

.EXAMPLE
    .\bump-version.ps1 -Bump patch -DryRun
    Shows what would change without writing files.
#>

param(
    [string]$Version = "",
    [ValidateSet("major", "minor", "patch")]
    [string]$Bump = "",
    [switch]$IncludeCli,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ── Paths ────────────────────────────────────────────────────────────
$repoRoot       = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$buildPropsPath = Join-Path $repoRoot "src\Directory.Build.props"
$pkgJsonPath    = Join-Path $repoRoot "tools\insights-cli\package.json"
$pkgLockPath    = Join-Path $repoRoot "tools\insights-cli\package-lock.json"

# ── Helpers ──────────────────────────────────────────────────────────
function Get-CurrentVersion {
    if (-not (Test-Path $buildPropsPath)) {
        throw "Directory.Build.props not found at $buildPropsPath"
    }
    $content = Get-Content -Path $buildPropsPath -Raw
    if ($content -match '<VersionPrefix[^>]*>(\d+\.\d+\.\d+)</VersionPrefix>') {
        return $Matches[1]
    }
    throw "Could not read VersionPrefix from Directory.Build.props"
}

function Assert-SemVer {
    param([string]$Value)
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must use major.minor.patch format (e.g. 1.2.3). Received: '$Value'."
    }
}

function Step-Version {
    param([string]$Current, [string]$Part)
    $parts = $Current.Split('.')
    switch ($Part) {
        "major" { $parts[0] = [int]$parts[0] + 1; $parts[1] = 0; $parts[2] = 0 }
        "minor" { $parts[1] = [int]$parts[1] + 1; $parts[2] = 0 }
        "patch" { $parts[2] = [int]$parts[2] + 1 }
    }
    return "$($parts[0]).$($parts[1]).$($parts[2])"
}

# ── Validate parameters ─────────────────────────────────────────────
if ($Version -and $Bump) {
    throw "Specify either -Version or -Bump, not both."
}
if (-not $Version -and -not $Bump) {
    throw "Specify either -Version '1.2.3' or -Bump (major|minor|patch)."
}

# ── Resolve new version ─────────────────────────────────────────────
$oldVersion = Get-CurrentVersion
Assert-SemVer -Value $oldVersion

if ($Bump) {
    $newVersion = Step-Version -Current $oldVersion -Part $Bump
} else {
    Assert-SemVer -Value $Version
    $newVersion = $Version
}

if ($newVersion -eq $oldVersion) {
    Write-Host "Version is already $oldVersion - nothing to do." -ForegroundColor Yellow
    exit 0
}

$label = if ($DryRun) { "[DRY RUN] " } else { "" }

Write-Host ""
Write-Host "${label}Bumping version: $oldVersion -> $newVersion" -ForegroundColor Cyan
Write-Host ""

# ── 1. Update Directory.Build.props ──────────────────────────────────
Write-Host "${label}  Updating src/Directory.Build.props" -ForegroundColor White
if (-not $DryRun) {
    $content = Get-Content -Path $buildPropsPath -Raw
    $content = $content -replace "(<VersionPrefix[^>]*>)$([regex]::Escape($oldVersion))(</VersionPrefix>)", "`${1}$newVersion`${2}"
    Set-Content -Path $buildPropsPath -Value $content -NoNewline
}

# ── 2. Update insights-cli package.json / package-lock.json ─────────
if ($IncludeCli) {
    # Read current CLI version before modifying
    $cliOldVersion = $null
    if (Test-Path $pkgJsonPath) {
        $pkgContent = Get-Content -Path $pkgJsonPath -Raw
        if ($pkgContent -match '"version"\s*:\s*"([^"]+)"') {
            $cliOldVersion = $Matches[1]
        }
    }

    if (Test-Path $pkgJsonPath) {
        Write-Host "${label}  Updating tools/insights-cli/package.json ($cliOldVersion -> $newVersion)" -ForegroundColor White
        if (-not $DryRun) {
            $json = Get-Content -Path $pkgJsonPath -Raw
            $json = $json -replace '("version"\s*:\s*")([^"]+)(")', "`${1}$newVersion`${3}"
            Set-Content -Path $pkgJsonPath -Value $json -NoNewline
        }
    }
    if ($cliOldVersion -and (Test-Path $pkgLockPath)) {
        Write-Host "${label}  Updating tools/insights-cli/package-lock.json" -ForegroundColor White
        if (-not $DryRun) {
            $lock = Get-Content -Path $pkgLockPath -Raw
            # Only replace the package's own version entries, not dependency versions
            $cliOldEscaped = [regex]::Escape($cliOldVersion)
            $lock = [regex]::Replace($lock, "(`"version`"\s*:\s*`")$cliOldEscaped(`")", "`${1}$newVersion`${2}")
            Set-Content -Path $pkgLockPath -Value $lock -NoNewline
        }
    }
}

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "${label}Done! Version is now $newVersion" -ForegroundColor Green
Write-Host ""
Write-Host "Files updated:" -ForegroundColor Gray
Write-Host "  - src/Directory.Build.props" -ForegroundColor Gray
if ($IncludeCli) {
    Write-Host "  - tools/insights-cli/package.json" -ForegroundColor Gray
    Write-Host "  - tools/insights-cli/package-lock.json" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  git add -A && git commit -m 'v$newVersion'" -ForegroundColor Gray
Write-Host "  .\scripts\publish.ps1" -ForegroundColor Gray
