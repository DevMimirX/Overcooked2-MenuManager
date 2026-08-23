# Overcooked2-MenuManager

`OC2MenuManager` is a standalone BepInEx plugin for Overcooked! 2. It provides scene-specific dish tracking, a menu-history overlay, prepared-dish tracking, order-ticket tinting and guesses, carnival menu helpers, and a built-in no-menu mode.

The runtime package contains one plugin DLL. It does not require HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, OC2DIYLevel, or Recipe Extension. The last two are optional integrations.

## Installation

Requirements:

- Overcooked! 2
- BepInEx, including its Harmony runtime

Copy `OC2MenuManager.dll` to `Overcooked! 2\BepInEx\plugins\OC2MenuManager\`, start the game, and press `F6` to open the settings window.

User guides:

- [English guide](docs/OC2MenuManager.md)
- [中文说明](docs/OC2MenuManager-zh.md)

## Repository layout

- `src/OC2MenuManager`: the .NET Framework 3.5 plugin
- `tests/OC2MenuManager.Tests`: pure migration and runtime-policy tests that do not load the game
- `third_party/refs`: the minimal compile-time game and loader references
- `eng`: the legacy plugin build contract and reference list
- `tools`: build, dependency-audit, version, and packaging scripts
- `.github/workflows`: pull-request CI and tag-only releases

The optional OC2DIYLevel and Recipe Extension integrations use audited soft-dependency metadata plus guarded reflection. Recipe Extension's generated pool, six-order option, prepared containers, No Menu behavior, and Carnival weighting are supported. Their absence is a supported no-op, and neither optional DLL becomes a runtime assembly dependency.

Runtime work is event-driven: team probability/overlay caches are invalidated by order, phase, catalog, or rule changes; prepared maintenance sleeps between queued callbacks and staged recovery; and recipe-heavy compatibility paths reuse their working buffers.

## Build and validation

Run from PowerShell:

```powershell
.\tools\Build.ps1 -Configuration Release -Package
```

The script restores the .NET Framework reference assemblies, builds the plugin, runs unit tests, validates the assembly dependency allowlist, and creates:

- `artifacts/Overcooked2-MenuManager-v1.1.1.zip`
- `artifacts/Overcooked2-MenuManager-v1.1.1-symbols.zip`
- `artifacts/Overcooked2-MenuManager-v1.1.1-SHA256SUMS.txt`

All entries under `third_party/refs` are marked non-copy-local and are excluded from packages. To audit the target game build without committing its full DLL, run:

```powershell
.\tools\Test-BaseGameCompatibility.ps1 -ReferenceRoot 'C:\path\to\Overcooked2-BaseGame\build-20236421'
```

See [the reference provenance note](third_party/README.md) and [the runtime smoke-test checklist](docs/SMOKE_TEST.md).

## Releases

Pushes and pull requests run CI without publishing a release. A tag must exactly match `PluginMetadata.Version`—currently `v1.1.1`—before the release workflow will publish the two validated zip files and their SHA256 checksum manifest.
