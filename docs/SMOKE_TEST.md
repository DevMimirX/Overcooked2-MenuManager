# Runtime smoke-test checklist

Use a disposable or backed-up BepInEx profile and fully close the game before changing plugin files.

## Standalone profile

1. Install only BepInEx and the packaged `OC2MenuManager.dll`; do not install HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, or OC2DIYLevel.
2. Start Overcooked! 2 and confirm the BepInEx log loads `com.ch3ngyz.plugin.OC2MenuManager` without missing-assembly errors.
3. Press `F6` and verify the settings window opens.
4. Enter a standard scene and verify dish selection, history overlay, prepared counts, ticket tinting, and guess tickets.
5. Exercise the carnival menu toggles and built-in no-menu mode, then return to the frontend without an exception.

## Compatibility data

1. Start with an existing `OC2MenuManager.standalone.cfg` and verify its values are retained.
2. With no `OC2MenuManager.selections.txt`, provide `HostUtilities-ServedDishTrackerSelections.txt` and start the game.
3. Verify `OC2MenuManager.selections.txt` is copied with identical contents and the legacy file remains unchanged.
4. Change both files, restart, and verify the existing `OC2MenuManager.selections.txt` wins and is not overwritten.

## Optional DIY integration

1. Run the standalone profile without OC2DIYLevel and open/refresh the scene selector; verify no error is logged.
2. Install OC2DIYLevel separately, restart, and verify its available levels appear after discovery.
3. Remove OC2DIYLevel again and verify Menu Manager still loads normally.
