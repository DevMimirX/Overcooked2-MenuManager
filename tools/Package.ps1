[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $repositoryRoot "src\OC2MenuManager\bin\$Configuration\OC2MenuManager.dll"
$symbolsPath = Join-Path $repositoryRoot "src\OC2MenuManager\bin\$Configuration\OC2MenuManager.pdb"
& (Join-Path $PSScriptRoot "Assert-StandaloneArtifact.ps1") -Configuration $Configuration -AssemblyPath $assemblyPath

if (-not (Test-Path -LiteralPath $symbolsPath -PathType Leaf)) {
    throw "Plugin symbols were not found: $symbolsPath"
}

$metadata = Get-Content -LiteralPath (Join-Path $repositoryRoot "src\OC2MenuManager\PluginMetadata.cs") -Raw
$versionMatch = [regex]::Match($metadata, 'Version\s*=\s*"(?<version>[^"]+)"')
if (-not $versionMatch.Success) {
    throw "Could not determine the plugin version."
}

$version = $versionMatch.Groups["version"].Value
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stagingRoot = Join-Path $artifactsRoot "staging"
$runtimeRoot = Join-Path $stagingRoot "OC2MenuManager"
$symbolsRoot = Join-Path $stagingRoot "symbols"
$runtimeZip = Join-Path $artifactsRoot "Overcooked2-MenuManager-v$version.zip"
$symbolsZip = Join-Path $artifactsRoot "Overcooked2-MenuManager-v$version-symbols.zip"

$resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') + '\'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
if (-not $resolvedStagingRoot.StartsWith($resolvedRepositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean staging outside the repository: $resolvedStagingRoot"
}

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $runtimeRoot, $symbolsRoot -Force | Out-Null
Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $runtimeRoot "OC2MenuManager.dll")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\OC2MenuManager.md") -Destination (Join-Path $runtimeRoot "README.md")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\OC2MenuManager-zh.md") -Destination (Join-Path $runtimeRoot "README.zh-CN.md")
Copy-Item -LiteralPath $symbolsPath -Destination (Join-Path $symbolsRoot "OC2MenuManager.pdb")

$runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeRoot -File | Sort-Object Name | ForEach-Object { $_.Name })
$expectedRuntimeFiles = @("OC2MenuManager.dll", "README.md", "README.zh-CN.md") | Sort-Object
if (Compare-Object -ReferenceObject $expectedRuntimeFiles -DifferenceObject $runtimeFiles) {
    throw "Unexpected runtime package contents: $($runtimeFiles -join ', ')"
}

foreach ($archivePath in @($runtimeZip, $symbolsZip)) {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}

Compress-Archive -LiteralPath $runtimeRoot -DestinationPath $runtimeZip -CompressionLevel Optimal
Compress-Archive -LiteralPath (Join-Path $symbolsRoot "OC2MenuManager.pdb") -DestinationPath $symbolsZip -CompressionLevel Optimal

Write-Host "Runtime package: $runtimeZip"
Write-Host "Symbols package: $symbolsZip"
