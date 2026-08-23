# Runtime smoke-test checklist

Use a disposable or backed-up BepInEx profile and fully close the game before changing plugin files.

## Standalone profile

1. Install only BepInEx and the packaged `OC2MenuManager.dll`; do not install HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, OC2DIYLevel, or Recipe Extension.
2. Start Overcooked! 2 and confirm the BepInEx log loads `com.ch3ngyz.plugin.OC2MenuManager` without missing-assembly errors.
3. Press `F6` and verify the settings window opens.
4. Enter a standard scene and verify dish selection, history overlay, prepared counts, ticket tinting, and guess tickets.
5. Set five guess tickets and keep five real tickets active. Verify successful delivery removes exactly one real ticket and rotates guesses; expiration plays the failure/reset behavior but keeps the same real ticket and does not change served history. Confirm the active total stays at ten and no negative-table or capacity warning repeats.
6. Exercise the carnival menu toggles and built-in No Menu mode, then return to the frontend without an exception.

## Compatibility data

1. Start with an existing `OC2MenuManager.standalone.cfg` and verify its values are retained.
2. With no `OC2MenuManager.selections.txt`, provide `HostUtilities-ServedDishTrackerSelections.txt` and start the game.
3. Verify `OC2MenuManager.selections.txt` is copied with identical contents and the legacy file remains unchanged.
4. Change both files, restart, and verify the existing `OC2MenuManager.selections.txt` wins and is not overwritten.

## Optional DIY integration

1. Run the standalone profile without OC2DIYLevel and open/refresh the scene selector; verify no error is logged.
2. Install OC2DIYLevel separately and restart. Before launching a DIY level, verify all DIY scenes appear after frontend initialization.
3. Keep `.png`, `.txt`, and other sidecar files beside the DIY bundles; verify they never appear as scenes in the selector.
4. Before launching `s_rw_5`, select it and verify all 50 configured dishes are available, including both original and custom recipes.
5. Save a tracked subset, restart without launching the level, and verify the subset persists.
6. Launch the level and verify custom placeholder names are upgraded to their runtime definitions without duplicate dish IDs; prepared matching and guess tickets should work afterward.
7. Change or replace a DIY metadata bundle in the disposable profile, restart, and verify removed recipes do not remain in that scene.
8. Remove OC2DIYLevel again and verify Menu Manager still loads normally.

## Recipe Extension and large pools

1. Start once with Recipe Extension installed but disabled; verify standard Menu Manager behavior and no generated recipes in the current catalog.
2. Enable Recipe Extension 1.1, start a supported level, and verify its generated recipes appear after round initialization.
3. Enable the extension's six-order option and Menu Manager's five-guess maximum. Keep six real tickets active, then serve and expire many consecutive orders; verify only four guesses remain active, the total stays at ten, every served ticket is destroyed, and expired tickets remain with reset timers.
4. Verify the 153-entry pool produces finite percentages, no duplicate selector rows, correct prepared matches, and no repeated compatibility warnings.
5. Test `5_6_Dynamic_Lvl_03` and `1_6_Dynamic_Lvl_01` across every phase; verify generated dishes follow Recipe Extension's phase-specific exclusions.
6. Run DIY level `s_rw_5` with Recipe Extension enabled and the five-guess maximum. Fill all eight real slots and verify guesses reduce to two before the incoming real tickets are created.
7. Serve and expire early, middle, and late `s_rw_5` orders. Verify successful score/history updates occur once and remove the matching ticket; expiration leaves score/history and the active ticket unchanged while resetting its timer. Confirm no invalid table release or unintended stuck order remains.
8. Run another DIY level with Recipe Extension enabled and verify both catalogs coexist without duplicate IDs or exceptions.
9. Disable Recipe Extension for the next round and verify generated-only selector entries are removed from the active scene catalog.
10. Disable Menu Manager history tracking while keeping Recipe Extension enabled. Fill the available real-order slots and verify every incoming real ticket still receives capacity and removes normally.
11. On `Day_3_4`, test Good Menu alone and Good Menu plus Good Cake. Verify generated recipes remain eligible, opening restrictions affect only the original Carnival indices, and forced-cake checkpoints select the original cake entries without corrupting extension frequencies.
12. Enable the fixed/TAS Carnival menu and verify its exact base-recipe sequence still takes precedence over Recipe Extension.

## No Menu lifecycle

1. Toggle No Menu in the frontend, start a standard campaign kitchen, and verify it activates only when the round starts.
2. Toggle it off and on during a round; verify the current round does not change and the status text reports the pending next-round state.
3. In standard and dynamic kitchens, deliver several valid dishes—including DIY and Recipe Extension dishes—and verify the original pipeline performs score, combo continuity, game-mode callbacks, client success, and one plate return per delivery.
4. On a dynamic level, advance through every phase and verify only that phase's recipes are accepted, including Recipe Extension's special phase filters.
5. Verify no startup orders remain and normal automatic order generation stays disabled while active.
6. Verify the history overlay, prepared counts, ticket colors, guesses, and tracker order hooks remain suspended for the active No Menu round.
7. Enter boss, tutorial, survival, Horde, pre-timer-order, public-online, and private-online cases. Verify No Menu stays inactive, the normal menu remains visible, and the settings status gives the reason.
8. Exercise local couch versus and verify both teams can deliver the same numeric synthetic order ID without colliding.
9. Force or simulate an injected-order failure and verify the server/client ghost ticket is cleaned, normal progression/UI is restored, and No Menu disables for the remainder of the round.
10. Leave the kitchen and verify recipe bars and ordinary order progression are restored for the next normal round.

## Team and probability behavior

1. In campaign, verify the overlay remains a single history/probability section.
2. In couch versus, make both teams receive numeric order ID `1`; verify Team 1 and Team 2 history, active counts, probabilities, and guesses remain independent while prepared counts are shared.
3. Expire the same ticket repeatedly, then trigger a failed delivery whose message carries a stale order ID. Verify neither event changes served history or active-menu counts.
4. Test ordinary, scripted-manual, scripted random fallback, dynamic phase reset, Carnival Good Menu/Good Cake checkpoints, and the fixed TAS sequence/fallback.
5. In a remote state that cannot be reconstructed unambiguously (for example duplicate entry IDs or unknown Carnival authority), verify probability renders as `—` and guesses are suppressed.

## Audited base build

1. Run `./tools/Build.ps1 -Configuration Release` and verify the normal build, tests, and standalone artifact allowlist pass without warnings.
2. Run `./tools/Test-BaseGameCompatibility.ps1 -ReferenceRoot 'C:\path\to\Overcooked2-BaseGame\build-20236421'`.
3. Verify the script accepts Steam build `20236421`, checks SHA256 `9BB6A3791331201D32CA89C3509F019A9780309DA7110002F04020E8491E1908`, compiles with a temporary reference set, and leaves the full game DLL outside the repository/package.

## Settled-round performance

1. Profile a settled round with Recipe Extension and a large catalog for at least one minute.
2. Verify Menu Manager does not patch or traverse `RecipeFlowGUI.LayoutWidgets` each frame.
3. Verify no steady-state scene-wide object scan occurs; prepared-source maintenance should sleep between callbacks/prunes, and scans should appear only as delayed, staged recovery when synchronization hooks found no source.
4. Add or remove an order in a 153-entry Recipe Extension pool and verify probability reconstruction occurs once per affected team; the overlay, prepared candidates, and guess-ticket sync must reuse that result until the next invalidating event.
5. In couch versus, verify each team retains an independent sorted-row/probability cache and switching overlay sections does not force the other team's cache to rebuild.
6. Repeat order changes for at least one minute and verify the ordered Recipe Extension snapshot is reflected only during round synchronization; collection/array allocations must not recur from entry expansion, reconstructed count sets, Carnival weight buffers, or prepared-candidate team lists after their initial capacity warm-up.
7. Disable history tracking and close `F6`; verify no controller lookup, ticket registration/reorder, prepared maintenance, or overlay rebuild remains in the steady-state frame path. Real-ticket admission and invalid `ReleaseTable` protection must still work.
8. On a non-16:9 resolution, verify DPI recalculation occurs only after an actual width/height change.
9. Dirty one prepared source and verify its delivered composition is simplified once per refresh, while recipe simplifications are reused.
10. Keep the settings window open and verify scene/category selector models are reused between source or selection changes.

## Log expectations

- Each optional adapter logs activation at most once.
- A changed or unsupported reflection contract logs one warning and disables only that integration.
- There should be no per-frame compatibility log spam, negative table index exception, missing optional assembly error, or repeated prepared-source scan in a settled round.
