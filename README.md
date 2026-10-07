# Apocaunloader

**Hold R to unload your gun** in Apocalypter (BepInEx 5 plugin).

Tapping the reload key reloads as usual; holding it plays the reload animation and moves the rounds from the gun back into your
backpack. Anything that does not fit in the backpack is dropped as an ammo box of the right caliber.
Switching to a smaller backpack or removing it also drops excess ammo instead of losing it to the game's capacity clamp.

## Features

- Works with all 17 guns (magazine, per-shell and three-phase reload styles) by replaying each gun's own reload FSM events, so the
  correct animation and sounds play.
- Ammo goes to the backpack up to the caliber's capacity; overflow spawns a matching ammo box (`ammo_box_762mm`, `…_556mm`,
  `…_9mm`, `…_12gauge`, `…_20gauge`, `…_22`, `…_3006`, arrows) registered like a normal item, so it saves.
- Backpack capacity reductions drop only the excess. Removal restores the game's base ammo capacities; the temporary empty
  slot during a swap does not trigger removal drops. Failed drops retain the ammo and retry once per second.
- Batteries drop as a battery pack containing the exact excess; grenades drop as individual items.
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocaunloader.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocaunloader.cfg`, section `[General]`:

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Turn the mod on/off |
| `DropBackpackOverflow` | `true` | Preserve excess ammo when changing/removing backpacks |
| `HoldSeconds` | `0.35` | Hold time that turns a reload into an unload (0.1–2) |
| `AnimationTimeout` | `4` | Seconds to wait for each animation phase before falling back (1–10) |
| `FallbackKey` | `R` | Key polled if the game's `Reload` input axis cannot be read |
| `VerboseLog` | `true` | Log every step to the BepInEx console/log |
| `Apocasetter` | `true` | Show in the Apocasetter Mods menu |

## Building

- `dotnet build` (override the game path with `-p:GameDir=...`); deploys to `BepInEx\plugins` after build, or
- `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars).

Use `-p:DeployToGame=false` to build without deployment. Run `dotnet run --project verification/LogicTests.csproj -c Release`
to check ammo conservation, capacity changes, failure retention and retries without launching the game.

## How it works

A Harmony prefix on `HutongGames.PlayMaker.Actions.GetButtonDown.OnUpdate` intercepts the `Reload` button. On a hold the mod finds the
active gun's `Reload` FSM, reads the caliber from its `checkAmmo` action, replays the FSM's `SendEvent` actions from the start state,
waits for the Animator events (`AnimFinished` / `AnimStart` / `AnimEnd`, seen through a prefix on `Fsm.ProcessEvent`), transfers
`ammo_in_gun` to `__GameManager__/Ammo`, and finally replays the `idle` state's events.

A prefix on `IntClamp.DoClamp` watches only `__GameManager__/Ammo` capacity reductions and drops overflow before vanilla
can discard it. Initial capacity observation establishes a baseline without spawning items during scene initialization.
A `FsmState.OnEnter` postfix watches the backpack slot's empty/full states and schedules base-capacity restoration after
actual removal, allowing a swap to finish first. Base capacities come from the game's `Backpack/set` actions.
