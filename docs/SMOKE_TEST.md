# Runtime smoke-test checklist

Use a disposable or backed-up BepInEx profile and fully close the game before changing plugin files.

## Standalone profile

1. Install only BepInEx and the packaged `OC2MenuManager.dll`; do not install HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, OC2DIYLevel, or Recipe Extension.
2. Start Overcooked! 2 and confirm the BepInEx log loads `com.ch3ngyz.plugin.OC2MenuManager` without missing-assembly errors.
3. Press `F6` and verify the settings window opens.
4. Enter a standard scene and verify dish selection, history overlay, prepared counts, ticket tinting, and guess tickets.
5. Set five guess tickets, then repeatedly serve and expire real orders. Verify real orders remain first, guesses rotate, and no negative-table or capacity warning repeats.
6. Exercise the carnival menu toggles and built-in No Menu mode, then return to the frontend without an exception.

## Compatibility data

1. Start with an existing `OC2MenuManager.standalone.cfg` and verify its values are retained.
2. With no `OC2MenuManager.selections.txt`, provide `HostUtilities-ServedDishTrackerSelections.txt` and start the game.
3. Verify `OC2MenuManager.selections.txt` is copied with identical contents and the legacy file remains unchanged.
4. Change both files, restart, and verify the existing `OC2MenuManager.selections.txt` wins and is not overwritten.

## Optional DIY integration

1. Run the standalone profile without OC2DIYLevel and open/refresh the scene selector; verify no error is logged.
2. Install OC2DIYLevel separately and restart. Before launching a DIY level, verify all DIY scenes appear after frontend initialization.
3. Select DIY levels containing original and custom recipes. Verify exact dishes are selectable, save a subset, restart, and verify the subset persists.
4. Launch those levels and verify custom placeholder names are upgraded to their runtime definitions without duplicate dish IDs; prepared matching and guess tickets should work afterward.
5. Change or replace a DIY metadata bundle in the disposable profile, refresh, and verify removed recipes do not remain in that scene.
6. Remove OC2DIYLevel again and verify Menu Manager still loads normally.

## Recipe Extension and large pools

1. Start once with Recipe Extension installed but disabled; verify standard Menu Manager behavior and no generated recipes in the current catalog.
2. Enable Recipe Extension 1.1, start a supported level, and verify its generated recipes appear after round initialization.
3. Enable the extension's six-order option and Menu Manager's five guesses. Keep six real tickets active, then serve and expire many consecutive orders; verify all eleven slots remain safe and guesses continue rotating.
4. Verify the 153-entry pool produces finite percentages, no duplicate selector rows, correct prepared matches, and no repeated compatibility warnings.
5. Test `5_6_Dynamic_Lvl_03` and `1_6_Dynamic_Lvl_01` across every phase; verify generated dishes follow Recipe Extension's phase-specific exclusions.
6. Run a DIY level with Recipe Extension enabled and verify both catalogs coexist without duplicate IDs or exceptions.
7. Disable Recipe Extension for the next round and verify generated-only selector entries are removed from the active scene catalog.

## No Menu lifecycle

1. Toggle No Menu in the frontend, start a standard campaign kitchen, and verify it activates only when the round starts.
2. Toggle it off and on during a round; verify the current round does not change and the status text reports the pending next-round state.
3. In standard and dynamic kitchens, deliver several valid dishes—including DIY and Recipe Extension dishes—and verify score, combo continuity, and one plate return per delivery.
4. On a dynamic level, advance through every phase and verify only that phase's recipes are accepted, including Recipe Extension's special phase filters.
5. Verify no startup orders remain and normal automatic order generation stays disabled while active.
6. Verify the history overlay, prepared counts, ticket colors, guesses, and tracker order hooks remain suspended for the active No Menu round.
7. Enter boss, tutorial, survival, Horde, pre-timer-order, and public-online cases. Verify No Menu stays inactive, the normal menu remains visible, and the settings status gives the reason.
8. Leave the kitchen and verify recipe bars and ordinary order progression are restored for the next normal round.

## Settled-round performance

1. Profile a settled round with Recipe Extension and a large catalog for at least one minute.
2. Verify Menu Manager does not patch or traverse `RecipeFlowGUI.LayoutWidgets` each frame.
3. Verify no steady-state scene-wide object scan occurs; prepared-source scans should appear only as delayed, staged recovery when synchronization hooks found no source.
4. On a non-16:9 resolution, verify DPI recalculation occurs only after an actual width/height change.
5. Dirty one prepared source and verify its delivered composition is simplified once per refresh, while recipe simplifications are reused.
6. Keep the settings window open and verify scene/category selector models are reused between source or selection changes.

## Log expectations

- Each optional adapter logs activation at most once.
- A changed or unsupported reflection contract logs one warning and disables only that integration.
- There should be no per-frame compatibility log spam, negative table index exception, missing optional assembly error, or repeated prepared-source scan in a settled round.
