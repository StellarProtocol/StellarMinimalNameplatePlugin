# Minimal Nameplate

A Stellar (BlueProtocol / Star Resonance) plugin that replaces the game's busy overhead nameplate with a
**minimal, role-colored class badge** — and an optional player name — floating over players' heads.

The overlay is drawn into the game's own HUD render pass, so it's **crisp** (rendered at native resolution,
after DLSS/FSR upscale) and **occluded by world geometry**, exactly like the game's real nameplate.

---

## What it does

- **Class badge** — a rounded, role-colored badge with the player's class/profession icon over each player's head.
  - Red = melee/tank line (prof 1/2/3/4/11), green = healer/support (5/13), blue = ranged/caster (9/12), gray = unknown.
- **Player name** (optional) — drawn under the badge (or on the head when the badge is off):
  - **cyan** = you / party members, **white** = other players, **red** = dead.
- **Dead state** — a downed player's badge grays out (class logo stays readable) and the name turns red and lifts.
- **Hides the game's own plate** for players while enabled, so you get *only* the minimal overlay — no double nameplate.
- **Mirrors the game's nameplate visibility** (see below) so the overlay appears/disappears exactly when the game's would.

## Visibility mirroring

The overlay honors every way the game itself hides a nameplate — three independent layers:

1. **Global HUD switch** — HideUI key, cutscenes, photo/camera mode, GM close, full-screen menus. When the game hides
   the whole HUD, the overlay draws nothing.
2. **Per-type setting** — the options-menu *"player / other-player head info"* toggles are respected per player.
3. **Per-entity hides** — individual plates the game hides, most notably **tag / hide-and-seek** (a hider's plate is
   hidden from seekers), plus disappear/dialog/layer-switch states. The overlay hides that player's badge too.

Distance/LOD culling and death are handled by the overlay itself (it deliberately tracks players slightly past the
game's nameplate range, and shows dead players in a distinct style).

---

## In-game controls

Open the **Nameplates** entry from the plugin launcher (window lives under *Tools*, top-right by default):

| Control | Description |
|---|---|
| **Enable Minimal Nameplate (Disable Game Nameplate)** | Master toggle. Off by default — turn on to replace the game plate with the overlay. |
| **Show Class Icon (badge)** | Draw the class badge. |
| **Show Player Name (under badge)** | Draw the player's name. |
| **Badge Size** | On-screen badge size (16–160 px at the reference distance). |
| **Name Size** | On-screen name size (16–160 px), independent of the badge. |

Settings persist to `stellar.minimalnameplate.config.json` in the game directory (keys `minimalnameplate_*`).

---

## Requirements

- **Stellar ModSystem framework `>= 1.1.0`** (the only floor-setting API used is `IFramework.ScreenWidth`; everything
  else is older/core).
- **Game build `release_3.7` (Season 3).** The overlay reflects into the game's IL2CPP types (`HudMgr`, `HudUtility`,
  `ZEntity`, `HudComp`, …), so it is tied to the game version, not the framework. Verified against `release_3.7`.
- **.NET 6** (SDK to build).

## Build & deploy

1. Copy `Local.props.example` → `Local.props` and set your game path:
   ```xml
   <GameInstallDir>E:\BPSR\StarLauncher\game\release_3.7\game_mini</GameInstallDir>
   ```
2. Build (Release):
   ```sh
   dotnet build Stellar.MinimalNameplate.csproj -c Release
   ```
   The post-build step copies `Stellar.MinimalNameplate.dll` to
   `<GameInstallDir>\stellar\plugins\minimalnameplate\` (and a local `plugins\minimalnameplate\`).
3. **Close the game before building** — a running game locks the loaded DLL. Plugins load at startup, so relaunch to
   pick up a new build.

---

## Project layout

| File | Responsibility |
|---|---|
| `Plugin.cs` | Plugin entry: config, the settings window, launcher entry. |
| `ClassIconOverlay.cs` | Overlay core — AOI player scan, head positions, throttles, per-frame update. |
| `ClassIconOverlay.EntityRead.cs` | Live `ZEntity` reads (profession / HP / dead) + the entity-dictionary walk, with the attribute-type and reuse perf contract. |
| `ClassIconOverlay.Icons.cs` | Piggyback class-icon sprite resolution — negative memo + scan backoff. |
| `ClassIconOverlay.Relation.cs` | Friend / guild (Union) relationship lookups for the name markers. |
| `ClassIconOverlay.HudPoc.cs` | Draws the badge + name into the game's `HudRenderPass` (crisp + depth-occluded). |
| `ClassIconOverlay.Visibility.cs` | Global + per-type nameplate-visibility mirror (`HudMgr.IsEnabled` / `GetHudSettingsShow`). |
| `ClassIconRules.cs` | Pure decision rules (attribute CLR type, sprite-scan backoff) — unit-tested. |
| `Stellar.MinimalNameplate.Tests/` | xUnit pins for `ClassIconRules`. Run with `dotnet test Stellar.MinimalNameplate.Tests`. |
| `NameplateIconPatch.cs` | Hides the game's own player plate (Harmony patches on `HudComp`). |
| `NameplateIconPatch.HudVisible.cs` | Tracks per-entity hides (tag/hide-and-seek, disappear, …) by patching `HudUtility` setters. |

## Diagnostics

Recurring diagnostics are off by default so a working install keeps `BepInEx\LogOutput.log` quiet. To troubleshoot
(e.g. after a game update), set `ClassIconOverlay.Diag = true` in source and rebuild — this re-enables the periodic
state dump and sprite-scan logging. One-time load/resolve lines (`resolve ok=`, `hud-vis api:`,
`per-entity hide mirror: N patched`, …) always log and are the first place to look if the overlay doesn't appear.

## Notes

- Characters are DOTS/ECS-rendered (no Unity `Transform` to parent to), so the badge position is polled each frame
  from the model anchor and billboarded toward the camera; size is dampened by distance to match the game's falloff.
- Class/profession icons are found among already-loaded sprites (a piggyback scan) rather than the shared async loader,
  so other plugins' icons are unaffected.

## Release notes

### 2.1.1

**What changed for you:** the class badges no longer make the game stutter. While you were in the world the overlay
was quietly triggering an error inside the game dozens of times a second — every one of those wrote a page to the
game's own log file — and it was also re-scanning every loaded image once a second, forever, whenever a class icon
couldn't be found. Both are fixed, so expect fewer hitches and freezes in crowded areas. Nothing about how the
badges look or behave has changed.

**Developer notes** (all four are hot-path fixes; no visual or behavioural change):

1. **`ZEntity.GetAttr<T>` closed over the wrong type for the profession attribute.** `AttrProfessionId` (220) is
   Int32-stored; the overlay read it through a `long` closure, so every call made the game `Debug.LogError` an
   `arr type err, type=Int64, enum=AttrProfessionId` line (stack capture, main thread) *and* return 0 — meaning the
   live profession read had never once worked and always fell through to the `EntityDetail` snapshot. Measured in the
   owner's Player.log: 42,353 such lines in one session, ~43/s. A second `MethodInfo` closed over `int` is now used
   for it; HP / max-HP keep the `long` closure (zero error lines named them). Pinned by `ClassIconRulesTests`.
2. **The sprite scan had no negative memo.** `Resources.FindObjectsOfTypeAll<Sprite>()` — a full loaded-object scan
   that materialises an Il2CppInterop wrapper per element — ran every second for the rest of the session whenever any
   tracked profession's icon was not loaded. A profession is now dropped after 3 fruitless scans and the interval
   backs off 1 → 2 → 5 → 10 s; a profession id never tracked before re-arms both.
3. **Dead state is resolved at most once per uuid per rebuild (2 Hz)** instead of up to three times per drawn badge
   per frame. A death or revive shows on the badge up to 0.5 s late — deliberate.
4. **The AOI rebuild is strictly 2 Hz.** The old gate (`_players.Count == 0 || timer >= 0.5`) re-ran the whole
   reflection walk of the entity dictionary *every frame* whenever the tracked list was empty. The walk also reuses
   its result buffer and caches its enumerator members. Per-frame reflection argument arrays are hoisted into reused
   fields (safe: every one of these paths runs only on the Unity main thread — see the perf contract comment at the
   top of `ClassIconOverlay.EntityRead.cs`).

---

## License

Licensed under the **GNU Affero General Public License v3.0** — see [`LICENSE.md`](LICENSE.md).
