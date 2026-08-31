[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Package
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $repositoryRoot "src\OC2MenuManager\OC2MenuManager.csproj"
$testProject = Join-Path $repositoryRoot "tests\OC2MenuManager.Tests\OC2MenuManager.Tests.csproj"

Push-Location $repositoryRoot
try {
    & dotnet restore $pluginProject
    if ($LASTEXITCODE -ne 0) { throw "Plugin restore failed." }

    & dotnet msbuild $pluginProject /t:Build /p:Configuration=$Configuration /m
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

    if (-not $SkipTests) {
        & dotnet test $testProject --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    & (Join-Path $PSScriptRoot "Assert-StandaloneArtifact.ps1") -Configuration $Configuration

    if ($Package) {
        & (Join-Path $PSScriptRoot "Package.ps1") -Configuration $Configuration
    }
}
finally {
    Pop-Location
}
