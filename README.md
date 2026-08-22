# Overcooked2-MenuManager

`OC2MenuManager` is a standalone BepInEx plugin for Overcooked! 2. It provides scene-specific dish tracking, a menu-history overlay, prepared-dish tracking, order-ticket tinting and guesses, carnival menu helpers, and a built-in no-menu mode.

The runtime package contains one plugin DLL. It does not require HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, or OC2DIYLevel.

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
- `tests/OC2MenuManager.Tests`: pure migration tests that do not load the game
- `third_party/refs`: the minimal compile-time game and loader references
- `eng`: the legacy plugin build contract and reference list
- `tools`: build, dependency-audit, version, and packaging scripts
- `.github/workflows`: pull-request CI and tag-only releases

The optional OC2DIYLevel integration uses guarded reflection and filesystem discovery. Its absence is a supported no-op and it is not an assembly or load-order dependency.

## Build and validation

Run from PowerShell:

```powershell
.\tools\Build.ps1 -Configuration Release -Package
```

The script restores the .NET Framework reference assemblies, builds the plugin, runs unit tests, validates the assembly dependency allowlist, and creates:

- `artifacts/Overcooked2-MenuManager-v1.0.0.zip`
- `artifacts/Overcooked2-MenuManager-v1.0.0-symbols.zip`

All entries under `third_party/refs` are marked non-copy-local and are excluded from packages. See [the reference provenance note](third_party/README.md) and [the runtime smoke-test checklist](docs/SMOKE_TEST.md).

## Releases

Pushes and pull requests run CI without publishing a release. A tag must exactly match `PluginMetadata.Version`—currently `v1.0.0`—before the release workflow will publish the two validated zip files.
