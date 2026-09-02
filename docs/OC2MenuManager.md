# OC2MenuManager

Chinese version: [OC2MenuManager-zh.md](./OC2MenuManager-zh.md)

## Overview

`OC2MenuManager` is a standalone menu helper mod for Overcooked! 2.
It provides its own in-game settings window and does not require `ConfigurationManager.dll` or `HostUtilities`.
If `OC2DIYLevel` or Recipe Extension (`OC2ManyRecipes`) is installed, guarded optional adapters add their dishes without making either mod mandatory.

Main features:

- dish tracking per scene
- menu history overlay on the left side of the screen
- prepared-dish tracking
- color tinting for the real order tickets at the top
- up to fifteen guess orders for off-menu candidates, wrapping into compact adjustable rows below the real orders when needed
- carnival-stage menu helpers
- built-in no-menu toggle

## Requirements

- `Overcooked! 2`
- `BepInEx`

Compatibility target: Steam build `20236421`. Maintainers can verify the reference manifest/hash and compile against that exact build without copying the full game DLL into the repository:

```powershell
.\tools\Test-BaseGameCompatibility.ps1 -ReferenceRoot 'C:\path\to\Overcooked2-BaseGame\build-20236421'
```

## Optional Mod Compatibility

- `OC2DIYLevel` 0.8-style and newer recipe-helper contracts are supported.
  - DIY scenes and their exact recipe IDs/names are read after the DIY frontend metadata initializes.
  - You can select a DIY scene and configure its tracked dishes before launching it.
  - Original DIY recipes can be fully hydrated in the frontend; custom recipes are preloaded by ID/name and upgraded to their real runtime definitions when the round starts.
- Recipe Extension / `OC2ManyRecipes` 1.1 is supported at runtime.
  - Its generated recipes are added automatically after the extension creates them during server/client round synchronization. They appear as one selector and overlay row per recipe ID; duplicate names retain their IDs for disambiguation.
  - A scene with no saved subset tracks newly generated IDs automatically. Once you save an explicit subset, later IDs remain unchecked until you select them; disabling the extension removes generated-only rows for that round without deleting those saved IDs.
  - Provider entry order and duplicate entries are preserved for probability balancing. If an enabled provider snapshot is incomplete or its runtime frequency shape does not match, probability displays `—`, guesses are suppressed, and No Menu disables safely for that round.
  - Its two special dynamic-level phase filters are preserved.
  - Real orders always keep their slots. Real tickets stay first, and each row greedily keeps every next ticket that fits at its configured native-size percentage before additional tickets wrap below.
  - A successfully served real ticket always completes removal even if another mod previously assigned it an invalid UI table index. Expiration follows the base game: the same ticket remains active and its timer resets.
  - On Carnival, non-fixed Good Menu and Good Cake rules use the full generated pool while applying their special restrictions only to the original Carnival recipes. The fixed/TAS sequence remains base-recipe-only.

Both integrations are optional. Menu Manager does not reference, bundle, or modify either provider DLL or its settings/collections. If an optional mod is absent or changes to an unknown reflection contract, only that integration is disabled and Menu Manager continues to run. Private-online tracking works when both clients have Recipe Extension enabled; ambiguous remote probability remains `—`. Public online follows Recipe Extension's own disabled behavior, and Horde remains outside Menu Manager tracking and No Menu scope.

## Installation

Minimum install:

1. Copy `OC2MenuManager.dll` into one of these folders:
   - `Overcooked! 2\BepInEx\plugins\`
   - `Overcooked! 2\BepInEx\plugins\OC2MenuManager\`
2. Start the game.

Optional file:

- `OC2MenuManager.pdb`
  - not required
  - only useful for debugging

## Files Used by the Mod

Plugin file:

- `Overcooked! 2\BepInEx\plugins\OC2MenuManager\OC2MenuManager.dll`

Main config:

- `Overcooked! 2\BepInEx\config\OC2MenuManager.standalone.cfg`

Hotkey config:

- `Overcooked! 2\BepInEx\config\OC2MenuManager.hotkey.txt`

Tracked-dish selections:

- `Overcooked! 2\BepInEx\config\OC2MenuManager.selections.txt`

Optional generated discovery report:

- `Overcooked! 2\BepInEx\config\OC2MenuManager.dish-catalog-report.txt`
- each row includes the stable category key, English and Chinese category labels, the inference source (`native`, `semantic`, `workflow`, `scene`, `structure`, or `fallback`), the initial category key, and concise decisive evidence

Older `HostUtilities-ServedDishTrackerSelections.txt` data is copied once when the new selections file does not exist. The legacy file is never deleted or overwritten.

## Opening the Settings Window

Default hotkey:

- `F6`

The hotkey is stored in:

- `BepInEx\config\OC2MenuManager.hotkey.txt`

Example file:

```txt
# OC2MenuManager hotkey config
# Edit the value after Hotkey= and save the file.
# Use Hotkey=None if you want to disable the launch hotkey.
Hotkey=F6
```

Common values:

- `Hotkey=F6`
- `Hotkey=F7`
- `Hotkey=Home`
- `Hotkey=None`

You can change the hotkey in either of these ways:

- click the hotkey row inside the mod window
- edit `OC2MenuManager.hotkey.txt` directly

While the settings window is open, it is fully opaque and modal for pointer input. A transparent full-screen input shield prevents clicks, drags, and scrolling from reaching the game menus underneath it, including the click used to close the window.

## Recommended First-Time Setup

1. Open the mod window with `F6`.
2. In `Tracked Dishes`, choose a scene.
3. Select the dishes you want to track.
4. Turn on `Enable Menu Tracking`.
5. Turn on `Show Floating Overlay` if you want the left-side panel; it is off by default.
6. Adjust `Max Guess Count` if you want extra guess orders after the real orders.
7. If you want prepared-dish recognition, turn on `Enable Prepared Tracking`.
8. If you want the real top order cards tinted, turn on `Ticket Colors`.
9. Adjust the advanced `Overlay` position and font settings until the panel fits your screen.

## Settings Window Layout

The current window sections are:

- `Tracked Dishes`
- `Menu History Tracker`
- `Tier Settings`
- `Features`
- `Overlay`
- `Interface`

### Tracked Dishes

This section controls what the tracker is allowed to care about.

What you can do here:

- choose a scene
- search by scene ID or either Chinese/English metadata name
- pick tracked dishes for that scene
- use scene-specific category buttons such as cake, burger, pizza, and so on
- use an additional Player 1-4 assignment row when `s_rw_5` exposes the exact supported 50-recipe catalog
- use bulk actions such as select all, clear, or refresh

Important behavior:

- outside a round, you can browse scenes freely
- during a round, the scene selector locks to the current scene
- this lock is temporary and does not replace your configured scene; the previous selection returns when the round ends
- this lock is intentional so you do not accidentally edit another level mid-run
- the expanded selector shows filtered/total and DIY counts; mouse wheel, arrow keys, Page Up/Down, Home/End, and Enter are supported
- background metadata refreshes preserve mouse and scrollbar positions instead of returning to the selected row
- the search remains active while you configure several scenes and clears when the settings window closes

DIY scenes appear once OC2DIYLevel finishes loading its frontend metadata. Menu Manager never scans the game directory, enumerates the `levels` folder, or loads DIY asset bundles itself. It accepts only OC2DIYLevel's in-memory catalog, so screenshots, text files, and other files beside the bundles are ignored. A temporary provider failure keeps the last valid catalog visible, while malformed or duplicate metadata entries are skipped and reported without hiding valid scenes. Selecting a scene lazily loads its recipes; a failed preload is retried automatically while the window is open, and the retry button forces another attempt. You no longer need to launch a DIY level once just to configure it.

For DIY scenes, category buttons are inferred from the complete recipe set. Existing native names keep their normal family; custom names are matched as whole tokens across underscores, CamelCase, whitespace, Chinese text, and common transliterations. A conservative workflow pass can correct a semantic outlier only when its preparation facet, recipe kind, reflected cooking or mixing process, and required/base components agree more strongly with another family containing multiple anchors than with its current family. A shared pot, mixer, cup, model, or icon alone never merges families, and ambiguous evidence keeps the semantic result. Unrecognized families can still group by repeated author prefixes or suffixes, shared nested components, or cooking/mixing/plating structure. Only recipes with neither meaningful names nor usable structure use `Other`. These groups never rewrite dish names, are recomputed only when authoritative DIY recipe metadata is loaded or refreshed, and remain attached when a metadata-only custom recipe is upgraded to its runtime definition.

`s_rw_5` also has a separate `Track by player` row derived from its published station assignment. Player 1 covers Hot Chocolate, Fruit Pie, and Pancake; Player 2 covers Cold Chocolate, Fruit Ice, Cherry Milk, and Donut; Player 3 covers Hot Fruit Drinks, Cake, and Fruit Platter; Player 4 covers Fruit Platter, Banana Milk, Strawberry Milk, Ice Milk, and Fruit Juice. Fruit Platters intentionally appear in both Player 3 and Player 4. These buttons are ordinary batch toggles: they change the same ID-based dish selection as the family buttons, are not persisted separately, and exclude Recipe Extension dishes. If the authored 50-recipe catalog changes incompatibly, the player row is hidden rather than showing a partial assignment.

Recipe Extension dishes are generated from the real level at round initialization, so they appear automatically in the current scene selector and in-round overlay after synchronization completes. If a recently changed extension configuration does not appear, start a new round and inspect the one-time `[Compatibility]` warning in the BepInEx log; a failed active snapshot is deliberately not replaced with a partial catalog.

### Menu History Tracker

This is the main tracking section.

#### Enable Menu Tracking

- is the master switch for history, probabilities, prepared tracking, floating-overlay eligibility, ticket colors, and guess-order selection
- leaves Carnival controls, No Menu, the settings window, and unconditional ticket-capacity safety fixes active when it is off
- keeps its existing saved value; only the visible label changed

#### Show Floating Overlay

- appears directly below `Enable Menu Tracking`
- defaults to `Off` when the setting has never been saved
- controls only the left-side presentation; history, prepared tracking, ticket colors, probabilities, selections, and guess orders continue while it is off
- applies immediately during a round and persists across rounds, scene changes, and game restarts

`Max Guess Count` is placed immediately below this toggle for quick access.

#### Enable Prepared Tracking

- tracks completed dishes that have not been served yet
- affects both the left overlay and the real top ticket colors
- also affects whether a guess order should still remain visible

Prepared tracking tries to recognize finished dishes from several places, such as:

- plated completed dishes
- some container-held completed dishes
- some carried completed dishes
- some cooker-completed dishes

Ingredient lists use the base game's order-insensitive delivery rule, so generated recipes that differ only by ingredient sequence remain compatible. Cooking method and completion state still have to match: a different cooking step, raw dish, burnt dish, or incorrect mixing state is not treated as prepared. One physical dish keeps one canonical accounting assignment, while every compatible recipe is covered for presentation. Every covered overlay row shows `[ v ]`, every compatible tracked real ticket receives the prepared tint, and every compatible guess is suppressed until that physical dish is served or removed.

This is one of the heavier features in the mod. If you need better performance, try disabling it first.

#### Ticket Colors

- enables color tinting on the real order cards at the top of the screen
- turning it off immediately restores existing real tickets without changing active guess tickets
- guess tickets always retain their configured `Guess Color` and opacity independently of this toggle
- turning it on immediately recolors existing tracked real tickets on the next repaint
- enabling `Enable Menu Tracking` during a round also discovers and recolors orders that were already visible
- if several live tickets are game-equivalent apart from ingredient ordering, a compatible prepared dish colors all of them without multiplying its numeric prepared count

Color rows:

- `On-Menu Color`
  - tracked dish is currently on the menu and not prepared
- `Prepared Color`
  - tracked dish is currently on the menu and already prepared
- `Guess Color`
  - extra guess orders injected into the real top ticket UI

Opacity:

- the `A` channel controls full-card opacity
- this affects the whole order card, not just the background
- guess orders are rendered a little dimmer than the chosen base color so they stay visually secondary

#### Max Guess Count

This row appears immediately below `Show Floating Overlay` and is shown only once in the settings window.

Range:

- `0` to `15`

Default:

- `5`

Meaning:

- `0` disables guess orders
- `1-15` sets the maximum number of extra guess orders after the real orders

Important notes:

- these are reference orders only
- they are not real game orders
- real orders stay first; guess orders are appended after them
- each row greedily keeps the next ordered ticket while it fits at that row's configured size
- row capacity changes with ticket size, measured card widths, spacing, and available HUD width; it is not capped at ten
- smaller scales can fit more than ten tickets in a row
- overflow real orders and guesses continue below without removing otherwise-valid guesses

#### First Row Ticket Size (%)

This row appears immediately below `Max Guess Count`.

Range and default:

- `50%` to `100%`
- default `90%`

Meaning:

- applies only to the first ticket row
- the percentage is a direct fraction of native ticket size; `100%` means native size
- the row keeps the largest consecutive real-then-guess prefix that fits before wrapping
- an unusually wide single ticket may shrink below the configured size only to prevent clipping
- values saved under the pre-rename Chinese setting migrate automatically; otherwise the new `90%` default applies

#### Lower Row Ticket Size (%)

This row appears immediately below `First Row Ticket Size (%)`.

Range and default:

- `50%` to `100%`
- default `70%`

Meaning:

- applies to every ticket row after the first, including overflow real orders
- the percentage is a direct fraction of native ticket size; `100%` means native size
- each lower row greedily keeps the largest consecutive prefix that fits before wrapping again
- lower rows clip the decorative blank header outside the top recipe tile instead of merely hiding it beneath the preceding row
- timer bars, recipe contents, card bodies, and animations remain visible because the crop boundary is the base game's top-tile rectangle
- existing lower-row scale values migrate to the renamed Chinese setting automatically

#### Display Language

Modes:

- `Auto`
- `English`
- `Chinese`

Behavior:

- `Auto` follows the game language
- this setting changes dish names, category names, the settings window text, and the overlay text

#### Scene Label Max Length

- controls text truncation for the scene selector and scene dropdown

#### Dish Label Max Length

- controls text truncation for the tracked-dish list in the main window

### Tier Settings

This section controls category priority.

How it works:

- each dish category has a tier from `1` to `6`
- lower tiers sort earlier
- click the tier button to cycle through values
- use `Reset` to restore one category
- use `Reset All` to restore every category
- inferred DIY families inherit the closest existing tier (for example, mixed drinks inherit Smoothie) and do not add dynamic configuration entries

Current UI behavior:

- categories are shown in five columns
- this keeps the section shorter vertically

What tiers affect:

- left overlay ordering
- guess-order ordering
- category grouping logic

What tiers do not affect:

- real game order generation
- the actual recipe content

This is a category-level setting, not a per-dish setting.

### Features

These are special gameplay-related toggles.

#### Carnival Better Menu

- removes onion from the first order
- keeps cake out of the first two carnival orders

#### Carnival Better Cakes

- raises the chance of cake orders on the carnival stage

#### Carnival TAS Menu

- locks the carnival stage to a TAS-oriented sequence

#### No Menu Mode

- changes apply at the next round boundary; the status line reports active, pending, or unsupported state
- supports standard and dynamic campaign kitchens plus local couch-versus kitchens
- disables normal order generation only after verifying that the round has no special startup orders
- accepts any recipe valid for the current round; dynamic levels follow the game's current phase
- supports DIY and Recipe Extension recipes
- injects a temporary team-scoped order and lets the original delivery pipeline handle plate return, matching, combo, scoring, game-mode callbacks, and client messages
- suspends Menu Manager’s overlay, prepared tracking, ticket tinting, and guess tickets while active
- boss, tutorial, survival, Horde, pre-timer-order, and every online round (public or private) retain their normal menu and show the reason

### Overlay

This section keeps the advanced position, size, font, alignment, and color controls for the in-game text panel. Panel visibility is controlled by `Show Floating Overlay` near the top of `Menu History Tracker`.

Available settings:

- `Overlay X`
- `Overlay Y`
- `Overlay Width`
- `Overlay Height`
- `Overlay Font Size`
- `Overlay Scene Name Length`
- `Overlay Dish Name Length`
- `Bold Overlay Font`
- `Overlay Dish Limit`
- `Overlay Text Align`
- `Overlay Font Color`
- `Served Count Color`
- `Probability Color`
- `Prepared Count Color`

Practical usage:

- if text is cut off, increase the scene-name and dish-name length limits
- if the panel feels crowded, reduce `Overlay Dish Limit`
- if the panel is too intrusive, move it or reduce width and font size

### Interface

This section controls the mod window itself.

Items:

- `UI Font Size`
  - base font size shared by the standalone window and the overlay
- `UI Font Color`
  - base font color shared by the standalone window and the overlay
- `Open Menu Manager`
  - hotkey row for remapping the settings window key

## How the Overlay Works

The left overlay is the compact todo-style panel. It is hidden by default until both `Enable Menu Tracking` and `Show Floating Overlay` are on. Turning only the overlay off does not clear or pause any tracking state, so turning it back on rebuilds the current view without resetting counts.

It shows:

- scene name
- sorting hint
- legend
- one row per tracked dish
- served count
- next-order probability
- prepared count when prepared tracking is enabled

Campaign presentation remains a single section. Couch versus displays independent `Team 1` and `Team 2` history/probability sections; prepared counts remain shared across the kitchen. If an authoritative next-order state is unavailable and cannot be reconstructed unambiguously, probability is shown as `—` and no guess ticket is created from it.

### Overlay Legend

Without prepared tracking:

- `[   ] On menu`
- `[ - ] Unprepared`
- `[ x ] Served`

With prepared tracking:

- `[   ] On menu`
- `[ - ] Unprepared`
- `[ x ] Served`
- `[ v ] Prepared`

Meaning:

- `On menu`
  - currently on the real menu and not prepared yet
- `Unprepared`
  - not on the real menu, not prepared, but still has positive chance to appear again
- `Served`
  - not on the real menu and current next-order probability is `0%`
- `Prepared`
  - completed already but not served yet

### Overlay Sorting

The overlay is designed to put the most actionable dishes first.

High-level priority:

1. on-menu and unprepared
2. off-menu, unprepared, probability above `0%`
3. off-menu, unprepared, probability `0%`
4. prepared

Within similar rows, it further sorts by:

- category tier
- next-order probability
- served count
- display name

### Visual Rules

- orange dish name means on-menu and unprepared
- green dish name means prepared
- blue numbers are served counts
- gold numbers are probabilities
- `0%` rows are dimmed and struck through

## How Guess Orders Work

Guess orders are extra reference tickets shown after the real order tickets.

They reuse the game’s ticket UI, but they are not real orders.

A dish can become a guess order only if all of the following are true:

- the dish is tracked
- the dish is not currently on the real menu
- the dish is not covered by any compatible prepared source
- the dish still has positive next-order probability

The guess-order list is built from the same sorted source as the `[ - ] Unprepared` rows in the left overlay.
That means:

- the order matches the left overlay’s off-menu candidate order
- the displayed guess tickets are a visual subset of those `[ - ]` rows

Current behavior:

- real orders stay at the front
- guess orders appear after them
- each row holds the largest consecutive ordered set that fits at its configured scale; overflow continues below
- incoming real orders keep all their capacity without evicting valid guesses
- when a guess becomes invalid, it disappears
- when a new candidate becomes eligible, it can fill the freed guess slot

## Language and Dish Naming

English naming in this mod is intentionally concise and category-first.

Examples:

- `Cake: Chocolate`
- `Pancake: Strawberry`
- `Burger: Cheese`

This keeps English names closer to the Chinese naming style and makes long dish names easier to scan in the UI.

The same language setting also changes category names and setting labels.

## Text Truncation Settings

If text looks cut off, these are the first settings to adjust:

- `Scene Label Max Length`
- `Dish Label Max Length`
- `Overlay Scene Name Length`
- `Overlay Dish Name Length`

They control:

- scene selector button and dropdown
- tracked-dish list in the settings window
- overlay scene title
- overlay dish names

## Performance Tips

If the mod feels heavy, try these changes first:

1. disable `Enable Prepared Tracking`
2. disable `Ticket Colors`
3. reduce `Max Guess Count`
4. reduce `Overlay Dish Limit`

Why:

- prepared-source matching remains the heaviest optional feature
- prepared composition changes are coalesced for two frames and processed at up to four dirty sources per frame; a backlog continues on the following frame, while bootstrap and pruning retain their slower schedules
- failed prepared-source reads or matches fail closed and receive three bounded retries at 15-frame intervals; distinct diagnostics are logged once per round and capped at eight
- scene/controller discovery is cached, and recovery scans are delayed and split across frames
- the scene search model is rebuilt only when its query or source catalog changes, and the expanded list draws only visible rows
- the game remains authoritative for ticket creation, timers, and removal; Menu Manager reparents only active presentation widgets when real/guess tickets require ordering, wrapping, compact lower-row scaling, or width fitting
- each team's next-order probabilities and sorted overlay rows are rebuilt once after an order/phase/rule change, then shared by the overlay, prepared matching, and guess tickets
- Recipe Extension's ordered generated-entry list is reflected once after round synchronization, then reused; large-pool expansion and remote reconstruction also reuse working buffers instead of allocating temporary collections on every refresh
- when history tracking is disabled and no tracker state needs cleanup, the frame update exits after hotkey/discovery housekeeping

## Troubleshooting

### The hotkey does not work

Check:

- `BepInEx\config\OC2MenuManager.hotkey.txt` exists
- the file does not say `Hotkey=None`
- another mod is not intercepting the same key

### I replaced the DLL but nothing changed in game

Check:

- you copied the DLL into the real game install, not a staging folder
- the game was fully closed before replacing the file
- there is not another `OC2MenuManager.dll` in a different `BepInEx\plugins` folder

Best practice:

1. close the game completely
2. verify the installed DLL path
3. replace the DLL
4. restart the game

### The scene selector is locked

This is normal during a round.
The mod locks the selector to the current scene while you are in-level.
The lock ends with the round and the last scene you explicitly selected becomes active again.

### The scene list jumps while scrolling

Metadata refreshes do not reposition the list. Opening the selector reveals the configured scene once, search changes return to the first result, and keyboard movement reveals only its keyboard target. Mouse-wheel and scrollbar positions otherwise remain under your control.

### The top guess orders are not the same as the real orders

This is expected.
Guess orders are only references.
They show off-menu candidates that still have a positive chance to appear.

### The tracker feels too crowded

Try:

- turning off `Show Floating Overlay` when you do not need the left panel
- lowering `Max Guess Count`
- lowering `Overlay Dish Limit`
- increasing the text-length settings
- moving the overlay

### A DIY scene is listed but has no dishes

Keep the settings window open briefly so the automatic retry can run, or press `Retry DIY Recipe Load` to retry immediately. Check the BepInEx log for one `[Compatibility]` warning if the installed DIY version exposes an unsupported contract or a recipe cannot be preloaded completely.

### A DIY scene seems to be missing

Open the scene selector and search for its scene ID, such as `s_rw_`. The count line distinguishes filtered, total, and DIY scenes, while the OC2DIYLevel status line shows whether metadata is ready, partial, loading, or using the last valid snapshot. Press `Refresh` to request another in-memory metadata read. Menu Manager deliberately does not fall back to scanning DIY folders.

### Recipe Extension dishes do not appear

They are generated at round initialization rather than in the frontend. Confirm `OC2ManyRecipes` 1.1 is enabled, start a new level, and wait for synchronization to complete before opening `F6`. Look for the one-time adapter-ready or compatibility-warning line in the BepInEx log. Null arrays for recipe categories unused by the level are normal; an invalid non-null array is rejected as a whole instead of exposing a misleading partial list.

### A served order remains visible

The mod reserves a valid UI table before every real ticket, including when history tracking is disabled. It also guards the base game's unchecked table release so a successfully delivered ticket inherited from an already-full UI can still animate out and be destroyed. Expired tickets intentionally remain and reset, matching build 20236421. If a delivered ticket remains, keep the BepInEx log and note the level, active real-order count, Recipe Extension options, and `Max Guess Count`.

## Summary

If you only want the essentials:

1. install `OC2MenuManager.dll`
2. start the game
3. press `F6`
4. choose a scene
5. pick the dishes to track
6. enable `Enable Menu Tracking`
7. optionally enable `Show Floating Overlay`, `Enable Prepared Tracking`, and `Ticket Colors`

That is enough for normal use.
