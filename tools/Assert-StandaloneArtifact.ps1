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

$buildDefinitionFiles = @(
    $projectPath,
    (Join-Path $repositoryRoot "Directory.Build.props"),
    (Join-Path $repositoryRoot "Directory.Build.targets"),
    (Join-Path $repositoryRoot "eng\LegacyGamePlugin.props")
)
$allowedDeclaredReferences = @(
    "0Harmony20",
    "Assembly-CSharp-nstrip",
    "BepInEx",
    "System",
    "System.Core",
    "UnityEngine",
    "UnityEngine.AnimationModule",
    "UnityEngine.CoreModule",
    "UnityEngine.IMGUIModule",
    "UnityEngine.TextRenderingModule",
    "UnityEngine.UI",
    "UnityEngine.UIModule"
)
$declaredReferences = @(
    foreach ($buildDefinitionFile in $buildDefinitionFiles) {
        $buildDefinitionText = Get-Content -LiteralPath $buildDefinitionFile -Raw
        if ($buildDefinitionText -match "<ProjectReference\b") {
            throw "The plugin build definition must not contain ProjectReference entries: $buildDefinitionFile"
        }
        foreach ($match in [regex]::Matches($buildDefinitionText, '<Reference\s+Include\s*=\s*"(?<name>[^",]+)')) {
            $match.Groups["name"].Value
        }
    }
) | Sort-Object -Unique
$unexpectedDeclaredReferences = @($declaredReferences | Where-Object { $_ -notin $allowedDeclaredReferences })
$missingDeclaredReferences = @($allowedDeclaredReferences | Where-Object { $_ -notin $declaredReferences })
if ($unexpectedDeclaredReferences.Count -gt 0 -or $missingDeclaredReferences.Count -gt 0) {
    throw "Compile reference declarations differ from the standalone allowlist. Unexpected: $($unexpectedDeclaredReferences -join ', '); missing: $($missingDeclaredReferences -join ', ')"
}

$allowedPluginPackages = @("Microsoft.NETFramework.ReferenceAssemblies.net35")
$declaredPluginPackages = @(
    foreach ($buildDefinitionFile in $buildDefinitionFiles) {
        $buildDefinitionText = Get-Content -LiteralPath $buildDefinitionFile -Raw
        foreach ($match in [regex]::Matches($buildDefinitionText, '<PackageReference\s+Include\s*=\s*"(?<name>[^",]+)')) {
            $match.Groups["name"].Value
        }
    }
) | Sort-Object -Unique
$unexpectedPluginPackages = @($declaredPluginPackages | Where-Object { $_ -notin $allowedPluginPackages })
$missingPluginPackages = @($allowedPluginPackages | Where-Object { $_ -notin $declaredPluginPackages })
if ($unexpectedPluginPackages.Count -gt 0 -or $missingPluginPackages.Count -gt 0) {
    throw "Plugin PackageReference declarations differ from the standalone allowlist. Unexpected: $($unexpectedPluginPackages -join ', '); missing: $($missingPluginPackages -join ', ')"
}

$referenceDirectory = Join-Path $repositoryRoot "third_party\refs"
$expectedReferenceDlls = @(
    "0Harmony20.dll",
    "Assembly-CSharp-nstrip.dll",
    "BepInEx.dll",
    "UnityEngine.dll",
    "UnityEngine.AnimationModule.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.UI.dll",
    "UnityEngine.UIModule.dll"
) | Sort-Object
$actualReferenceDlls = @(
    Get-ChildItem -LiteralPath $referenceDirectory -File -Filter "*.dll" |
        ForEach-Object { $_.Name }
) | Sort-Object
$referenceDllDifference = @(Compare-Object -ReferenceObject $expectedReferenceDlls -DifferenceObject $actualReferenceDlls)
if ($referenceDllDifference.Count -gt 0) {
    throw "third_party/refs must contain exactly the standalone compile-reference allowlist. Found: $($actualReferenceDlls -join ', ')"
}

$forbiddenSourcePatterns = @(
    "^\s*using\s+HostUtilities\s*;",
    "^\s*namespace\s+HostUtilities\b",
    "\bOC2Mods\.Shared\b",
    "\bConfigurationManager\b",
    "PluginRuntimeContext",
    "\bBepInDependency\b",
    "\bAssembly\s*\.\s*Load(?:File|From)?\s*\("
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
