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
- extra top-row guess orders for off-menu candidates
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
  - Its generated recipes are added after the extension creates them for the round.
  - Its two special dynamic-level phase filters are preserved.
  - Real orders always keep their slots; active real and guess tickets are capped at ten whenever the level itself stays within that limit.
  - A successfully served real ticket always completes removal even if another mod previously assigned it an invalid UI table index. Expiration follows the base game: the same ticket remains active and its timer resets.
  - On Carnival, non-fixed Good Menu and Good Cake rules use the full generated pool while applying their special restrictions only to the original Carnival recipes. The fixed/TAS sequence remains base-recipe-only.

Both integrations are optional. If an optional mod is absent or changes to an unknown reflection contract, only that integration is disabled and Menu Manager continues to run.

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

## Recommended First-Time Setup

1. Open the mod window with `F6`.
2. In `Tracked Dishes`, choose a scene.
3. Select the dishes you want to track.
4. Turn on `Enable History Tracking`.
5. If you want prepared-dish recognition, turn on `Enable Prepared Tracking`.
6. If you want the real top order cards tinted, turn on `Ticket Colors`.
7. Adjust `Max Guess Count` if you want extra guess orders on the top row.
8. Adjust the `Overlay` position and font settings until the left-side panel fits your screen.

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
- pick tracked dishes for that scene
- use scene-specific category buttons such as cake, burger, pizza, and so on
- use bulk actions such as select all, clear, or refresh

Important behavior:

- outside a round, you can browse scenes freely
- during a round, the scene selector locks to the current scene
- this lock is intentional so you do not accidentally edit another level mid-run

DIY scenes appear once OC2DIYLevel finishes loading its frontend metadata. Only scenes confirmed by that metadata are listed, so screenshots, text files, and other files beside the bundles are ignored. Selecting a scene lazily loads its recipes; a failed preload is retried automatically while the window is open, and the retry button forces another attempt. You no longer need to launch a DIY level once just to configure it.

Recipe Extension dishes are generated from the real level at round initialization, so they appear in the current scene selector after that round starts. Press `Refresh` if a recently changed optional-mod configuration has not appeared yet.

### Menu History Tracker

This is the main tracking section.

#### Enable History Tracking

- enables the tracker on standard levels
- drives the left overlay
- drives guess-order selection for the top row

#### Enable Prepared Tracking

- tracks completed dishes that have not been served yet
- affects both the left overlay and the real top ticket colors
- also affects whether a guess order should still remain visible

Prepared tracking tries to recognize finished dishes from several places, such as:

- plated completed dishes
- some container-held completed dishes
- some carried completed dishes
- some cooker-completed dishes

This is one of the heavier features in the mod. If you need better performance, try disabling it first.

#### Ticket Colors

- enables color tinting on the real order cards at the top of the screen

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

Range:

- `0` to `5`

Default:

- `3`

Meaning:

- `0` disables top-row guess orders
- `1-5` sets the maximum number of extra guess orders after the real orders

Important notes:

- these are reference orders only
- they are not real game orders
- real orders stay first; guess orders are appended after them
- the active guess count is `max(0, min(configured maximum, 10 - active real orders))`
- five active real orders allow five guesses, six allow four, and eight allow two
- a level that creates more than ten real orders still shows every real order and uses zero guesses

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

This section controls the in-game text panel on the left side.

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

The left overlay is the compact todo-style panel.

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

Guess orders are the extra reference tickets shown on the real top order bar.

They reuse the game’s ticket UI, but they are not real orders.

A dish can become a guess order only if all of the following are true:

- the dish is tracked
- the dish is not currently on the real menu
- the dish is not prepared
- the dish still has positive next-order probability

The top guess-order list is built from the same sorted source as the `[ - ] Unprepared` rows in the left overlay.
That means:

- the order matches the left overlay’s off-menu candidate order
- the top row is effectively a visual subset of those `[ - ]` rows

Current behavior:

- real orders stay at the front
- guess orders appear after them
- excess guesses disappear before a newly arriving real order is added
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
- prepared maintenance runs only when a source changes, a prune is due, or a delayed recovery stage is scheduled; it is not a recipe scan performed every frame
- scene/controller discovery is cached, and recovery scans are delayed and split across frames
- ticket layout is left to the game; Menu Manager only reorders membership when the real/guess set changes
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

### The top guess orders are not the same as the real orders

This is expected.
Guess orders are only references.
They show off-menu candidates that still have a positive chance to appear.

### The tracker feels too crowded

Try:

- lowering `Max Guess Count`
- lowering `Overlay Dish Limit`
- increasing the text-length settings
- moving the overlay

### A DIY scene is listed but has no dishes

Keep the settings window open briefly so the automatic retry can run, or press `Retry DIY Recipe Load` to retry immediately. Check the BepInEx log for one `[Compatibility]` warning if the installed DIY version exposes an unsupported contract or a recipe cannot be preloaded completely.

### Recipe Extension dishes do not appear

They are generated at round initialization rather than in the frontend. Start the level, open `F6`, and use `Refresh`. Confirm `OC2ManyRecipes` 1.1 is enabled and look for the one-time adapter activation line in the BepInEx log.

### A served order remains visible

Version 1.1.1 reserves a valid UI table before every real ticket, including when history tracking is disabled. It also guards the base game's unchecked table release so a successfully delivered ticket inherited from an already-full UI can still animate out and be destroyed. Expired tickets intentionally remain and reset, matching build 20236421. If a delivered ticket remains, keep the BepInEx log and note the level, active real-order count, Recipe Extension options, and `Max Guess Count`.

## Summary

If you only want the essentials:

1. install `OC2MenuManager.dll`
2. start the game
3. press `F6`
4. choose a scene
5. pick the dishes to track
6. enable `Enable History Tracking`
7. optionally enable `Enable Prepared Tracking` and `Ticket Colors`

That is enough for normal use.
