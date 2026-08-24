$ErrorActionPreference = "Stop"

$installerPath = Join-Path $PSScriptRoot "..\..\scripts\install.ps1"
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Could not parse install.ps1."
}

$definition = $ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Assert-Checksum"
}, $true) | Select-Object -First 1
if (-not $definition) {
    throw "Assert-Checksum was not found in install.ps1."
}
Invoke-Expression $definition.Extent.Text

$assetName = "ai-usage-tray-win-x64-v2.4.0.zip"
$expectedHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

function Invoke-WebRequest {
    param($Uri, $Headers, [switch]$UseBasicParsing)
    return [PSCustomObject]@{
        Content = [Text.Encoding]::UTF8.GetBytes("$expectedHash  $assetName`n")
    }
}

function Get-FileHash {
    param($LiteralPath, $Algorithm)
    return [PSCustomObject]@{ Hash = $expectedHash }
}

Assert-Checksum -ZipPath "unused.zip" -ChecksumUrl "https://example.test/checksum" -AssetName $assetName
Write-Host "PASS: Assert-Checksum accepts a UTF-8 byte array response."
