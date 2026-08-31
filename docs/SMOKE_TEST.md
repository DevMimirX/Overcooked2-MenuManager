# Runtime smoke-test checklist

Use a disposable or backed-up BepInEx profile and fully close the game before changing plugin files.

## Standalone profile

1. Install only BepInEx and the packaged `OC2MenuManager.dll`; do not install HostUtilities, OC2Mods.Shared, ConfigurationManager, OC2NoMenu, OC2DIYLevel, or Recipe Extension.
2. Start Overcooked! 2 and confirm the BepInEx log loads `com.ch3ngyz.plugin.OC2MenuManager` without missing-assembly errors.
3. Press `F6` and verify the settings window opens.
4. Place the window over frontend buttons and click, drag, scroll, and close it repeatedly. Verify the fully opaque panel remains interactive while no underlying button highlights, activates, starts a level, or receives the closing click; clicks outside the panel must also remain blocked until it closes.
5. With a clean configuration, verify history tracking is available but `Show Floating Overlay` is off and the left panel stays hidden in a standard scene. Turn it on and verify dish selection, history overlay, prepared counts, ticket tinting, and guess tickets.
6. Set five guess tickets and keep five real tickets active. Verify successful delivery removes exactly one real ticket and rotates guesses; expiration plays the failure/reset behavior but keeps the same real ticket and does not change served history. Confirm the active total stays at ten and no negative-table or capacity warning repeats.
7. Exercise the carnival menu toggles and built-in No Menu mode, then return to the frontend without an exception.

## Floating overlay quick controls

1. Confirm `Show Floating Overlay` appears directly below `Enable Menu Tracking`, `Max Guess Count` appears immediately below it exactly once, and the advanced position, size, font, alignment, and color controls remain in `Overlay`.
2. In a standard round, serve and prepare dishes with the overlay on. Turn it off and confirm the panel disappears on the next repaint while served/on-menu/prepared counts, probabilities, selected dishes, ticket colors, and guess orders continue unchanged.
3. Turn the overlay back on and confirm the current view rebuilds on the next update without resetting any state.
4. Change the toggle, finish the round, change scenes, and restart the game. Verify the saved value persists. Remove only the `显示悬浮窗` key from a disposable configuration and verify it defaults to off again.
5. Verify disabled history tracking, active No Menu, Horde, out-of-round state, and a missing or empty current-scene catalog suppress the panel even when `Show Floating Overlay` is on.
6. Move `Max Guess Count` through `0-5`; verify `0` removes guesses, each other value remains effective, and overlay visibility never changes the guess count.

## Live ticket tint controls

1. Confirm the visible master row is named `启用菜单追踪 / Enable Menu Tracking`; the existing persisted `启用历史菜单追踪` value must remain unchanged after upgrading.
2. In a standard round with several tracked and untracked real orders plus guess tickets, turn `Ticket Colors` off. Verify only real widgets restore their original tint, opacity, interaction, and raycast state on the next repaint. Active guesses must retain exactly the same configured guess color and opacity, and history, probabilities, prepared counts, selections, and guesses must remain unchanged.
3. Turn `Ticket Colors` back on and verify all existing tracked real tickets recolor on the next repaint without waiting for a new order. Guess tickets must remain visually unchanged and untracked real tickets must remain at their original color.
4. Disable `Enable Menu Tracking`, allow more real orders to appear, then re-enable it. Verify every existing real ticket is discovered through its active-order UI token and recolored immediately.
5. Repeat the activation test in couch versus with both teams using the same recipe ID, and with Recipe Extension six-ticket mode plus enough guesses to fill the ten-ticket bar. Verify each team remains correctly registered and no ticket collection is reordered or modified by reconciliation.
6. Repeat several off/on cycles with translucent colors. Verify no CanvasGroup leak, opacity drift, stuck interaction/raycast setting, repeated warning, or steady-state ticket scan appears.
7. Confirm out-of-round, No Menu, Horde, disabled tracking, temporarily unavailable controllers, and an intentionally broken reflection contract leave base-game visuals safe; transient controller availability retries at the bounded interval and a missing contract logs once.

## Ingredient-order prepared tinting

1. In a generated-recipe level, activate two tracked recipe IDs whose definitions accept the same physical completed dish. Prepare one valid dish and verify it keeps exactly one canonical numeric assignment while both overlay rows show `[ v ]`, both compatible real tickets receive the prepared tint, and neither ID remains in the guess tickets.
2. Disable `Enable Prepared Tracking` without changing the dish, tickets, or probabilities. Verify both otherwise-eligible guesses return normally; re-enable it and verify compatible coverage suppresses them again.
3. Add and remove a compatibility-only alternative while the canonical assignment remains unchanged. Verify overlay rows, real-ticket tints, and guesses update for that alternative without changing the physical count.
4. Repeat with two identical recipe-ID tickets and in couch versus across both team bars. Verify all compatible real tickets tint, prepared accounting remains kitchen-wide, and guess-ticket tint and opacity do not change.
5. Move the prepared dish between a cooker, carrier, and plate. Verify the source transfer neither duplicates nor loses the canonical physical count. Remove or serve it—including a carried `ItemPropertiesComponent`/`IngredientPropertiesComponent` source—and verify all compatibility coverage disappears immediately, all tints clear, and every otherwise-eligible guess returns.
6. Use the same ingredients with a different cooking-step ID, raw progress, burnt progress, an incorrect mixing state, and a plate with a different plating-step asset. Verify none is covered; then use the correct cooking step, completed cooking or mixing progress, and exact matching plate step and verify it is. Confirm an unplated completed container remains eligible before a plate is chosen.
7. In DIY level `s_rw_5`, track Strawberry Milk `[19991007]` and Cherry Milk `[19991029]`. Complete each recipe in `utensil_mixer_01` while the bowl's unrelated cooking state remains raw; verify the completed mixed composition records exactly one prepared dish and every compatible overlay row, live ticket, and guess updates within three frames.
8. Move each RW5 dish from the mixer bowl through a carrier and plate. Verify its canonical count neither duplicates nor disappears during transfer, then remove it and verify coverage and guess suppression disappear.
9. Dirty twelve registered prepared sources on the same frame. Verify the first changes appear after the two-frame debounce, no more than four sources are matched per frame, and the entire backlog drains within five frames without rescanning completed sources.
10. In a disposable provider stub, make source reading and recipe matching throw. Verify coverage fails closed, each source receives exactly three additional retries at 15-frame intervals, and each distinct scene/source/stage/exception diagnostic appears once per round with no more than eight entries. Normal empty, raw, incomplete, and unmatched dishes must not log warnings.
11. Repeat with a mixed recipe completed in a blender and cooked recipes completed in pots and pans, with Recipe Extension enabled and disabled. Verify raw, burnt, wrong-step, and incompletely mixed dishes remain uncovered, while correctly completed dishes work in native and DIY levels; missing OC2DIYLevel and malformed custom definitions must remain safe.
12. Exercise exact `WildcardOrderNode` direction and a valid cooked-container fallback. Verify only matches accepted by the audited base-game direction qualify, and container unwrapping never crosses cooking-step IDs or completion states.
13. Force one reference-ticket removal to fail before its active GUI entry detaches. Verify the ticket remains accounted as a reference, synchronization retries once the failure clears, and no occupied table or widget is reclassified as a real order or leaked after cleanup.
14. During an active RW5 round, track a small explicit subset and wait for its guess tickets to appear. Uncheck one visible guessed dish, clear the selection, and repeat with a category toggle. Verify every newly untracked guess begins removal immediately, no untracked recipe replaces it, and reselecting a recipe makes it eligible again without waiting for another real order or probability event.

## Compatibility data

1. Start with an existing `OC2MenuManager.standalone.cfg` and verify its values are retained.
2. With no `OC2MenuManager.selections.txt`, provide `HostUtilities-ServedDishTrackerSelections.txt` and start the game.
3. Verify `OC2MenuManager.selections.txt` is copied with identical contents and the legacy file remains unchanged.
4. Change both files, restart, and verify the existing `OC2MenuManager.selections.txt` wins and is not overwritten.

## Optional DIY integration

1. Run the standalone profile without OC2DIYLevel and open/refresh the scene selector; verify no error is logged.
2. Install the audited OC2DIYLevel binary separately and restart. Before launching a DIY level, verify every provider-reported scene appears once and the selector's DIY count matches the provider snapshot.
3. With the current `rw_v0.5` pack, search `s_rw_` and verify the six results are exactly `s_rw_1` through `s_rw_6`; repeat with one Chinese and one English metadata name, then select each result with one click.
4. Keep `.png`, `.txt`, and other sidecar files beside the DIY bundles; verify they never appear as scenes in the selector and no Menu Manager directory enumeration appears in an I/O trace.
5. Before launching `s_rw_5`, select it and verify all 50 configured dishes are available in exactly these groups: Cake 5, Pancake 4, Fruit Pie 5, Donut 4, Hot Chocolate 6, Cold Chocolate 5, Milk Drinks 3, Ice Milk 3, Fruit Juice 3, Hot Fruit Drinks 5, Fruit Ice 2, and Fruit Platter 5. `HotStrawberryMilk` must be in Hot Chocolate, `ColdMilk` must be in Cold Chocolate, and no recipe should be in `Other`.
6. Verify `Track by player / 按玩家批量勾选` appears above the family buttons with Player 1-4 counts `15, 12, 15, 13`. Confirm Player 2 contains the two Fruit Ice recipes and Cherry Milk `[19991029]`, Player 4 contains the three Fruit Juices plus Banana Milk and Strawberry Milk but excludes Cherry Milk, the five Fruit Platters belong to both Player 3 and Player 4, and the unique union remains exactly 50 recipes.
7. Press Clear, toggle each player independently, combine multiple players, and toggle them off again. Verify unrelated selections remain unchanged; turning Player 3 or Player 4 off also deselects their shared Fruit Platters and makes the other overlapping toggle incomplete.
8. Switch between English and Chinese and verify both player and family memberships remain unchanged. Toggle each family category off and on and confirm it still affects exactly its canonical group.
9. Save a player-derived tracked subset, restart without launching the level, and verify only the resulting recipe IDs persist; no player-group state or category data should appear in the selections file.
10. Launch the level and verify custom placeholder names upgrade to runtime definitions without changing player or family membership; prepared matching and guess tickets should work afterward.
11. Enable Recipe Extension in `s_rw_5` and verify generated dishes remain available through family/individual controls but never enter Player 1-4. The player groups must continue to cover only the original 50 DIY-authored IDs.
12. In a disposable provider profile, reorder the same recipes and refresh metadata; verify keys, labels, ordering, and membership remain identical. Remove or add an authored RW5 recipe and verify the entire player row hides without an in-game warning while family controls continue working; restore the exact catalog and verify it returns.
13. In a disposable provider stub/profile, exercise malformed recipe evidence, a partial malformed catalog, a duplicate scene ID, and a temporary read failure. Verify valid scenes remain, failures are summarized once, and the last successful snapshot survives only the temporary/untrustworthy reads.
14. Remove a valid DIY scene from authoritative metadata and refresh; verify the removed scene does not leak back from the hydrated scene cache. An authoritative empty catalog must also replace the prior snapshot.
15. Inspect `OC2MenuManager.dish-catalog-report.txt` and verify each DIY row includes its category key, bilingual labels, initial category key, decisive evidence, and one of the six documented inference sources. The two workflow-corrected RW5 rows must report `workflow` and their original `milkdrink` key.
16. Launch dynamic DIY level `s_rw_6` and cross every map switch. Verify orders added in each phase enter history, current-phase probabilities and prepared matching use that phase's recipe set, and previously served history remains intact.
17. Repeat after delaying the first tracked order until after a map switch, and after any order-controller reconstruction available in the test setup. Verify the new tracker run inherits the latest announced phase instead of reverting to phase zero.
18. Remove OC2DIYLevel again and verify Menu Manager still loads normally.

## Scene selector usability

1. Open the inline selector with the full base-game and provider-reported DIY catalog; verify it uses about 55% of the settings-window height and remains bounded between 160 and 420 pixels.
2. Verify the result count, Clear, Refresh, mouse wheel, Up/Down, Page Up/Down, Home/End, Enter, Escape, and single-click selection behaviors.
3. Confirm the current/highlighted item scrolls into view, the search persists between scene selections, and closing the settings window clears it.
4. Scroll away from the selected scene and keep the dropdown open for at least three 120-frame catalog polls. Verify mouse-wheel and scrollbar positions do not jump, and that wheel input over the scene list does not move the outer settings view; press Refresh and verify the position is preserved or only clamped if results shrink.
5. Select `s_rw_5`, play it, then finish or leave the round. Select another DIY and a base-game scene and verify each choice and dish panel survive repeated catalog polls. During an active round, verify the selector remains locked and restores the last explicit selection afterward.
6. Change a search while scrolled and verify it starts at the first result. Enter only spaces, scroll away, press Clear, and verify both the list and keyboard target return to the first result. Reorder catalog entries in a disposable provider profile and verify the keyboard target follows its scene ID.
7. Test a 1696x349 or equivalent short viewport. Verify the settings window is fully clamped on-screen, its outer content remains scrollable, and the scene list remains usable.

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
10. Disable `Enable Menu Tracking` while keeping Recipe Extension enabled. Fill the available real-order slots and verify every incoming real ticket still receives capacity and removes normally.
11. On `Day_3_4`, test Good Menu alone and Good Menu plus Good Cake. Verify generated recipes remain eligible, opening restrictions affect only the original Carnival indices, and forced-cake checkpoints select the original cake entries without corrupting extension frequencies.
12. Enable the fixed/TAS Carnival menu and verify its exact base-recipe sequence still takes precedence over Recipe Extension.
13. Verify generated dishes appear automatically in both `F6` and the in-round overlay after server/client synchronization, without pressing Refresh. Confirm identical display names with different IDs remain separate and show their IDs.
14. With no saved subset for a scene, start a round and verify all newly generated IDs are tracked. Save an explicit subset, regenerate a different pool, and verify newly discovered IDs are unchecked; re-enable a previously disabled pool and verify its saved IDs return unchanged.
15. In a disposable copy of Recipe Extension, simulate a null or empty `recipePatches` list, a null provider object, an invalid non-null `entries` array, and a list that changes during collection. Verify each active failure logs once, remains retryable, exposes no partial rows, renders probability as `—`, suppresses guesses, and disables No Menu for that round. Let a transient failure recover while the round remains active and verify the catalog/probabilities appear after the bounded retry. Confirm null arrays for unused categories remain valid.
16. In private online, enable Recipe Extension on both clients and verify generated orders are tracked; ambiguous remote probability remains `—`. In public online, verify Recipe Extension's own disabled state produces no generated rows.
17. Enter Horde with both mods enabled. Verify Menu Manager creates no tracking overlay or No Menu state, throws no compatibility exception, and does not alter Recipe Extension's Horde behavior.

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
10. Exercise a no-loading-screen full scene transition (the direct `GameUtils.LoadScene` path) and verify prior-round synthetic IDs, ticket state, hidden recipe bars, and disabled auto-progression do not leak into the next scene. Also verify additive `InGameMenu` loading does not reset the active round.
11. Leave the kitchen and verify recipe bars and ordinary order progression are restored for the next normal round.

## Team and probability behavior

1. In campaign, verify the overlay remains a single history/probability section.
2. In couch versus, make both teams receive numeric order ID `1`; verify Team 1 and Team 2 history, active counts, probabilities, and guesses remain independent while prepared counts are shared.
3. Expire the same ticket repeatedly, then trigger a failed delivery whose message carries a stale order ID. Verify neither event changes served history or active-menu counts.
4. Test ordinary, scripted-manual, scripted random fallback, dynamic phase reset, Carnival Good Menu/Good Cake checkpoints, and the fixed TAS sequence/fallback.
5. In a remote state that cannot be reconstructed unambiguously (for example duplicate entry IDs or unknown Carnival authority), verify probability renders as `—` and guesses are suppressed.
6. Make an enabled Recipe Extension snapshot disagree with the authoritative cumulative-frequency length. Verify Carnival control returns to Recipe Extension, while Menu Manager probability remains `—` and no guesses are created.

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
7. Disable `Enable Menu Tracking` and close `F6`; verify no controller lookup, ticket registration/reorder, prepared maintenance, or overlay rebuild remains in the steady-state frame path. Real-ticket admission and invalid `ReleaseTable` protection must still work.
8. On a non-16:9 resolution, verify DPI recalculation occurs only after an actual width/height change.
9. Dirty one prepared source and verify its delivered composition is simplified once per refresh, while recipe simplifications are reused.
10. Keep the settings window open and verify scene/category selector models are reused between source or selection changes; with the full scene dropdown open, only visible rows plus one overscan row per edge should be drawn.

## Log expectations

- Each optional adapter logs activation at most once.
- A changed or unsupported reflection contract logs one warning and disables only that integration.
- There should be no per-frame compatibility log spam, negative table index exception, missing optional assembly error, or repeated prepared-source scan in a settled round.
