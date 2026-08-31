[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReferenceRoot,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$expectedBuildId = "20236421"
$expectedAssemblyHash = "9BB6A3791331201D32CA89C3509F019A9780309DA7110002F04020E8491E1908"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $repositoryRoot "src\OC2MenuManager\OC2MenuManager.csproj"
$referenceSource = Join-Path $repositoryRoot "third_party\refs"
$resolvedReferenceRoot = (Resolve-Path -LiteralPath $ReferenceRoot).Path
$manifestPath = Join-Path $resolvedReferenceRoot "manifest.json"
$assemblyPath = Join-Path $resolvedReferenceRoot "Managed\Assembly-CSharp.dll"

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The base-game reference manifest was not found: $manifestPath"
}

if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "The target Assembly-CSharp.dll was not found: $assemblyPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.steam.buildId -ne $expectedBuildId -or [string]$manifest.steam.targetBuildId -ne $expectedBuildId) {
    throw "Expected Steam build $expectedBuildId, but the manifest identifies build '$($manifest.steam.buildId)' targeting '$($manifest.steam.targetBuildId)'."
}

$assemblyRecord = @($manifest.assemblies) | Where-Object { $_.name -eq "Assembly-CSharp.dll" } | Select-Object -First 1
if ($null -eq $assemblyRecord) {
    throw "The manifest does not contain an Assembly-CSharp.dll record."
}

$manifestHash = ([string]$assemblyRecord.sha256).ToUpperInvariant()
if ($manifestHash -ne $expectedAssemblyHash) {
    throw "The build manifest has an unexpected Assembly-CSharp.dll hash: $manifestHash"
}

$actualHash = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $manifestHash) {
    throw "Assembly-CSharp.dll does not match its manifest. Expected $manifestHash, got $actualHash."
}

$actualSize = (Get-Item -LiteralPath $assemblyPath).Length
if ([long]$assemblyRecord.sizeBytes -ne $actualSize) {
    throw "Assembly-CSharp.dll size does not match its manifest. Expected $($assemblyRecord.sizeBytes), got $actualSize."
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("OC2MenuManager-build-$expectedBuildId-" + [Guid]::NewGuid().ToString("N"))
$resolvedTempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
if (-not $resolvedTemporaryRoot.StartsWith($resolvedTempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a temporary reference directory outside the system temp root: $resolvedTemporaryRoot"
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    foreach ($referenceFile in Get-ChildItem -LiteralPath $referenceSource -File) {
        Copy-Item -LiteralPath $referenceFile.FullName -Destination (Join-Path $temporaryRoot $referenceFile.Name)
    }

    Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $temporaryRoot "Assembly-CSharp-nstrip.dll") -Force

    & dotnet restore $pluginProject
    if ($LASTEXITCODE -ne 0) {
        throw "Plugin restore failed."
    }

    & dotnet msbuild $pluginProject /t:Rebuild "/p:Configuration=$Configuration" "/p:GameRefsRoot=$temporaryRoot"
    if ($LASTEXITCODE -ne 0) {
        throw "Compilation against Overcooked! 2 build $expectedBuildId failed."
    }

    Write-Host "Verified Overcooked! 2 Steam build $expectedBuildId."
    Write-Host "Assembly-CSharp.dll SHA256: $actualHash"
    Write-Host "Exact-build compilation succeeded with temporary references only."
}
finally {
    if (Test-Path -LiteralPath $resolvedTemporaryRoot) {
        $verifiedTemporaryRoot = [System.IO.Path]::GetFullPath($resolvedTemporaryRoot)
        $verifiedTemporaryLeaf = [System.IO.Path]::GetFileName($verifiedTemporaryRoot)
        $isVerifiedTempChild = $verifiedTemporaryRoot.StartsWith($resolvedTempBase, [System.StringComparison]::OrdinalIgnoreCase)
        $hasExpectedPrefix = $verifiedTemporaryLeaf.StartsWith("OC2MenuManager-build-$expectedBuildId-", [System.StringComparison]::Ordinal)
        if ($isVerifiedTempChild -and $hasExpectedPrefix) {
            Remove-Item -LiteralPath $verifiedTemporaryRoot -Recurse -Force
        }
    }
}
