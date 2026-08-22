[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$AssemblyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\OC2MenuManager\OC2MenuManager.csproj"
$sourceRoot = Join-Path $repositoryRoot "src\OC2MenuManager"
if ([string]::IsNullOrEmpty($AssemblyPath)) {
    $AssemblyPath = Join-Path $sourceRoot "bin\$Configuration\OC2MenuManager.dll"
}

if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
    throw "Plugin assembly was not found: $AssemblyPath"
}

$projectText = Get-Content -LiteralPath $projectPath -Raw
if ($projectText -match "<ProjectReference\b") {
    throw "The plugin project must not contain ProjectReference entries."
}

$forbiddenSourcePatterns = @(
    "^\s*using\s+HostUtilities\s*;",
    "^\s*namespace\s+HostUtilities\b",
    "PluginRuntimeContext"
)

$sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter "*.cs"
foreach ($pattern in $forbiddenSourcePatterns) {
    $match = $sourceFiles | Select-String -Pattern $pattern | Select-Object -First 1
    if ($null -ne $match) {
        throw "Standalone source boundary violation at $($match.Path):$($match.LineNumber): $($match.Line.Trim())"
    }
}

$allowedReferences = @(
    "0Harmony20",
    "Assembly-CSharp",
    "BepInEx",
    "mscorlib",
    "System",
    "System.Core",
    "UnityEngine.AnimationModule",
    "UnityEngine.CoreModule",
    "UnityEngine.IMGUIModule",
    "UnityEngine.TextRenderingModule",
    "UnityEngine.UI",
    "UnityEngine.UIModule"
)

$assembly = [System.Reflection.Assembly]::LoadFile([System.IO.Path]::GetFullPath($AssemblyPath))
$referenceNames = @($assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name } | Sort-Object -Unique)
$unexpectedReferences = @($referenceNames | Where-Object { $_ -notin $allowedReferences })
if ($unexpectedReferences.Count -gt 0) {
    throw "Unexpected plugin assembly references: $($unexpectedReferences -join ', ')"
}

$requiredReferences = @("0Harmony20", "Assembly-CSharp", "BepInEx")
$missingReferences = @($requiredReferences | Where-Object { $_ -notin $referenceNames })
if ($missingReferences.Count -gt 0) {
    throw "Expected platform references are missing: $($missingReferences -join ', ')"
}

$outputDirectory = Split-Path -Parent $AssemblyPath
$outputDlls = @(Get-ChildItem -LiteralPath $outputDirectory -File -Filter "*.dll")
if ($outputDlls.Count -ne 1 -or $outputDlls[0].Name -ne "OC2MenuManager.dll") {
    throw "Release output must contain exactly one DLL: OC2MenuManager.dll. Found: $($outputDlls.Name -join ', ')"
}

Write-Host "Standalone artifact verified."
Write-Host "Assembly references: $($referenceNames -join ', ')"
