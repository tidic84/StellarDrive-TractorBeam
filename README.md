# Parking Tractor Beam

A MelonLoader mod for **StellarDrive (Demo)**: tractor beam landing pads for big ships. A smaller ship flies into the pad's zone, the beam catches it and parks it on the carrier, and it becomes part of the carrier until it leaves again.

This mod used to be called **Tractor Beam**: remove `Mods/TractorBeam.dll` when you install it (both at once register the same parts twice and the world never loads). Pad settings and landed ships from Tractor Beam 0.2 are kept.

![Parking Tractor Beam](Mods/Thumbnail.png)

## Why

A ship parked on another ship is two rigid bodies pressing on each other. Their hulls collide every physics tick, the small ship jitters, slides off when the carrier moves, and sometimes gets launched or damaged. Parking Tractor Beam joins the landed ship to the carrier with the game's own joint system, the one docking doors and rotors use: its colliders move under the carrier's rigidbody, so the two hulls are one body and never collide. Its mass is added to the carrier's, and it rides along with the carrier wherever it goes.

## How to use it

1. Build a **Tractor beam** pad on the carrier, on a floor. It is in the build menu next to the docking door and is unlocked by the same research. The pad's size sets the biggest zone and the biggest ship it takes:

   | Pad | Biggest zone (and ship) | The zone may be moved up to |
   | --- | --- | --- |
   | Small | 12 × 12 m, 10 m high | 30 m from the pad |
   | Medium | 24 × 24 m, 18 m high | 60 m from the pad |
   | Large | 48 × 48 m, 30 m high | 120 m from the pad |

2. Press **E** on the pad to open its menu. A camera beside the settings shows the carrier from outside (drag to turn around it, scroll to zoom), and the zone and parking spot are drawn around the carrier through the hull while the menu is open.
   - **Landing zone** tab: left/right, back/front and down/up from the pad, and its width, depth and height, in steps of 1 or 5 m. By default the zone stands on the pad; put it outside the hull, in front of a hangar door, under the ship... It is drawn in yellow.
   - **Parking spot** tab: where landed ships are set down (left/right, back/front, down/up from the pad, within the same reach as the zone) and which way their nose points: towards the pad's front, right, back or left, or *Auto* (the quarter turn nearest to the ship's heading as it comes in). It is drawn in violet: the space the biggest ship the pad takes would fill, and its footprint with an arrow towards the nose.
   - **Show zones**: *Always* (near them), *When piloting* (only at the controls of a ship) or *Never*. This is your own setting (`[ParkingTractorBeam] ShowZones` in `UserData/MelonPreferences.cfg`); the pad whose menu is open always shows its zone.
3. Fly your ship into the zone. Its outline turns green when the ship is in enough to be caught (see below), the box where the ship will be parked is drawn at the parking spot, and the screen shows `[L] Request landing`.
4. Press **L**. The beam brakes the ship and carries it to the parking spot: straight if the way is clear, otherwise across then down, or up over the hull then across then down. It turns the ship upright, faces it as the pad says and sets it down. Its controls and thrusters are cut while it is held and while it is landed.
5. While a ship sits on a pad (or the beam is busy with one), **that pad's zone is switched off completely**: no outline, no prompt, and a ship flying into it is not caught.
6. To leave, sit at the controls and press **L** again. The ship is lifted off the pad, carried back along the same way to where the beam caught it, and released at the carrier's speed.

![The landing prompt](docs/prompt.jpg)

The key can be changed in `UserData/MelonPreferences.cfg` (`[ParkingTractorBeam] LandKey`, an Input System path such as `<Keyboard>/l`).

## What the host checks

The host decides every landing. If something is wrong, the pilot is told what:

- the ship must be in the zone: its centre inside, and sticking out by no more than 5 m (a quarter of the zone's width on zones wider than 20 m);
- relative speed under 100 m/s (the beam stops a fast ship within 10 m), and no fast spin;
- the ship must be lighter than the carrier and small enough for the pad (its footprint and height, plus room to hover);
- the ship must fit at the parking spot without touching the hull around it;
- the path must be clear: the ship's own collider boxes are tested along the way against the carrier's hull and parts, the ground, other ships and players standing in the way;
- the ship must not already be joined to another one by a docking door or a rotor, or be anchored (StellarAnchor).

![The beam carrying a ship](docs/beam.jpg)

The mod is in English only.

## Saving and multiplayer

Landed ships are saved in `tractorbeam-docks.txt` and the pads' zones and parking spots in `tractorbeam-pads.txt`, next to `world.json`. If the first file is missing, a ship found sitting in a carrier's frame with no rotor or docking door holding it is kept on the nearest free pad.

The host runs the beam and keeps the zones; every player sees them. One player at a time can open a pad's menu. The host and every player need the mod. **Launch your landed ships before removing the mod**, or they stay in the carrier's frame.

## With Ship Performance Fix

Parking Tractor Beam runs alongside [Ship Performance Fix](https://github.com/tidic84/StellarDrive-ShipPerformanceFix). Ship Performance Fix freezes the inertia of big ships, so the carrier's inertia is recomputed one tick after a ship lands or leaves. Its corner-collider culling already ignores the colliders of a ship joined to the same body.

## Install

With [Stellar Mod Manager](https://github.com/jollyname/StellarModManager), or extract `StellarDrive-ParkingTractorBeam.zip` into the game folder (MelonLoader 0.7.x).

## Building

```bash
./build.sh    # Release build + dist/StellarDrive-ParkingTractorBeam.zip (GAME=... to point at another game folder)
```

`dev.sh` builds with `DEV`, starts the game and opens a test world; `cmd.sh` sends dev commands (see `src/ParkingTractorBeam/Dev/TractorDev.cs`): `pad`, `place`, `bring`, `land`, `launch`, `pilot`, `press`, `docks`, `where`, `cols`, `save`, `later`, `zoneset`, `parkset`, `bringzone`, `tab`, `orbit`, `mat`, `usepad`, `uiclick`, `shot`…
