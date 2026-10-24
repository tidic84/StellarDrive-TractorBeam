# Tractor Beam

A MelonLoader mod for **StellarDrive (Demo)**: tractor beam landing pads for big ships. A smaller ship flies into the pad's zone, the beam catches it and sets it down on the carrier, and it becomes part of the carrier until it leaves again.

![Tractor Beam](Mods/Thumbnail.png)

## Why

A ship parked on another ship is two rigid bodies pressing on each other. Their hulls collide every physics tick, the small ship jitters, slides off when the carrier moves, and sometimes gets launched or damaged. Tractor Beam joins the landed ship to the carrier with the game's own joint system, the one docking doors and rotors use: its colliders move under the carrier's rigidbody, so the two hulls are one body and never collide. Its mass is added to the carrier's, and it rides along with the carrier wherever it goes.

## How to use it

1. Build a **Tractor beam** pad on the carrier, on a floor with free space above it. It is in the build menu next to the docking door and is unlocked by the same research.

   | Pad | Landing zone above the pad |
   | --- | --- |
   | Small | 12 × 12 m, 10 m high |
   | Medium | 24 × 24 m, 18 m high |
   | Large | 48 × 48 m, 30 m high |

   The outline of the zone appears when you get near it.
2. Fly your ship into the zone. While you pilot it, the screen shows `[L] Request landing` when the whole ship is inside.
3. Press **L**. The beam brakes the ship, turns it upright, lines it up with the pad (to the nearest quarter turn) and sets it down. Its controls and thrusters are cut while it is held and while it is landed.
4. To leave, sit at the controls and press **L** again. The ship is lifted off the pad, carried back to where the beam caught it, and released at the carrier's speed.

![The landing prompt](docs/prompt.jpg)

The key can be changed in `UserData/MelonPreferences.cfg` (`[TractorBeam] LandKey`, an Input System path such as `<Keyboard>/l`).

## What the host checks

The host decides every landing. If something is wrong, the pilot is told what:

- the whole ship must be inside the zone;
- relative speed under 12 m/s, and no fast spin;
- the ship must be lighter than the carrier and small enough for the pad (its footprint and height, plus room to hover);
- the pad must be free;
- the path must be clear: the ship's own collider boxes are tested along the way against the carrier's hull and parts, the ground, other ships and players standing in the way;
- the ship must not already be joined to another one by a docking door or a rotor, or be anchored (StellarAnchor).

![The beam carrying a ship](docs/beam.jpg)

## Saving and multiplayer

Landed ships are saved in `tractorbeam-docks.txt` next to `world.json`. If that file is missing, a ship found sitting in a carrier's frame is kept on the nearest free pad.

The host runs the beam and every player sees it. The host and every player need the mod. **Launch your landed ships before removing the mod**, or they stay in the carrier's frame.

## With Ship Performance Fix

Tractor Beam runs alongside [Ship Performance Fix](https://github.com/tidic84/StellarDrive-ShipPerformanceFix). Ship Performance Fix freezes the inertia of big ships, so the carrier's inertia is recomputed one tick after a ship lands or leaves. Its corner-collider culling already ignores the colliders of a ship joined to the same body.

## Install

With [Stellar Mod Manager](https://github.com/jollyname/StellarModManager), or extract `StellarDrive-TractorBeam.zip` into the game folder (MelonLoader 0.7.x).

## Building

```bash
./build.sh    # Release build + dist/StellarDrive-TractorBeam.zip (GAME=... to point at another game folder)
```

`dev.sh` builds with `DEV`, starts the game and opens a test world; `cmd.sh` sends dev commands (see `src/TractorBeam/Dev/TractorDev.cs`): `pad`, `place`, `bring`, `land`, `launch`, `pilot`, `press`, `docks`, `where`, `cols`, `save`, `later`, `shot`…
