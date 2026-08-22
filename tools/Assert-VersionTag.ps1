[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $repositoryRoot "src\OC2MenuManager\PluginMetadata.cs"
$metadata = Get-Content -LiteralPath $metadataPath -Raw
$versionMatch = [regex]::Match($metadata, 'Version\s*=\s*"(?<version>[^"]+)"')
if (-not $versionMatch.Success) {
    throw "Could not read PluginMetadata.Version from $metadataPath"
}

$expectedTag = "v$($versionMatch.Groups['version'].Value)"
if ($Tag -cne $expectedTag) {
    throw "Release tag '$Tag' does not match plugin version '$expectedTag'."
}

Write-Host "Release tag matches plugin version: $expectedTag"
