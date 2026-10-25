#if DEV
using System.Globalization;
using System.Linq;
using Core.Services;
using Players.LocalClient.Controls;
using Ships;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Stellar.Shared;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Docking;
using UnityEngine;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Services;

namespace ParkingTractorBeam
{
    /// <summary>
    /// Dev commands (UserData/stellar_tractor_cmd.txt):
    ///   load &lt;world&gt;                       open a save from the main menu
    ///   ships                              every ship: parts, mass, space, joint parent
    ///   pad &lt;ship&gt; &lt;1|2|3&gt; [x z]          put a pad on the ship's top at ship-local (x, z) (default: centre)
    ///   bring &lt;ship&gt; &lt;carrier&gt; [h] [yaw]    put a ship in the zone of the carrier's first pad, h m above it
    ///   land / launch &lt;ship&gt;               the pilot's request, for that ship
    ///   docks                              records and beams
    ///   where &lt;ship&gt;                       pose, velocity, space and joint
    ///   tp &lt;ship&gt; x y z                    the local player to a ship-local point (in the ship's relative space)
    ///   look &lt;ship&gt; x y z                  point the camera at a ship-local point
    ///   shot [name]                        screenshot to UserData
    /// </summary>
    public partial class TractorMod
    {
        private static DevHarness _harness;

        private static float Fl(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private void DevInit()
        {
            _harness = new DevHarness("tractor", Log, autoLaunch: false);
            _harness.Command("load", a =>
            {
                string file = System.IO.Path.Combine(Application.persistentDataPath, "WorldSaves", string.Join(" ", a.Skip(1)), "world.json");
                Managers.Interface.MainGameSceneLoadData.Mode = Managers.Interface.MainGameSceneLoadData.GameMode.SinglePlayer;
                Managers.Interface.MainGameSceneLoadData.SaveFileFullName = new System.IO.FileInfo(file).FullName;
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainGame", UnityEngine.SceneManagement.LoadSceneMode.Single);
            });
            _harness.Command("partids", a =>
            {
                // partids: every part settings id at or above 5000, with its name (to pick free ids)
                var ids = Resources.FindObjectsOfTypeAll<PartSettings>().Where(p => p.id >= 5000).GroupBy(p => p.id).OrderBy(g => g.Key);
                Log.Msg("[DEV] part ids: " + string.Join(", ", ids.Select(g => $"{g.Key}={g.First().name}")));
            });
            _harness.Command("ships", a =>
            {
                var joints = Svc.JointsServer;
                foreach (TrackedShipServer s in Svc.ShipsServer.Ships.OrderBy(s => s.Id))
                {
                    string parent = joints != null && joints.TryGetShipJoint(s.Id, out TrackedShipJoint j) && j.Parent != null ? $" child of {j.Parent.ShipId} ({j.JointConnection})" : "";
                    Log.Msg($"[DEV] ship {s.Id}: {s.PartCount} parts, {s.HullAndPartsMass:F0} kg, space {s.SpaceId} ({Svc.SpacesServer.GetSpace(s.SpaceId)}), loaded {s.IsRigidbodyLoaded}, pads {Pads.PadsOf(s).Count()}{parent}");
                }
            });
            _harness.Command("pad", a =>
            {
                // pad <ship> <1|2|3> [x z]
                Place(uint.Parse(a[1]), Pads.Specs[int.Parse(a[2]) - 1].Id, a.Length > 4 ? Fl(a[3]) : float.NaN, a.Length > 4 ? Fl(a[4]) : float.NaN, false);
            });
            _harness.Command("place", a =>
            {
                // place <ship> <settings id> [x z] [inside]: a part on the hull under that ship-local column (inside: the lowest floor)
                Place(uint.Parse(a[1]), ushort.Parse(a[2]), a.Length > 4 ? Fl(a[3]) : float.NaN, a.Length > 4 ? Fl(a[4]) : float.NaN, a.Length > 5 && a[5] == "inside");
            });
            _harness.Command("bring", a =>
            {
                var ships = Svc.ShipsServer;
                TrackedShipServer ship = ships.GetShip(uint.Parse(a[1])), carrier = ships.GetShip(uint.Parse(a[2]));
                var pad = Pads.PadsOf(carrier).First();
                float h = a.Length > 3 ? Fl(a[3]) : 4f, yaw = a.Length > 4 ? Fl(a[4]) : 20f;
                var (pp, pr) = Pads.Frame(pad);
                Transform pt = carrier.RigidbodyObject.transform;
                var b = ship.GetHullCoordBounds();
                Quaternion rot = pt.rotation * pr * Quaternion.Euler(0f, yaw, 0f);
                Vector3 pos = pt.TransformPoint(pp + pr * Vector3.up * (h + b.Extents.y)) - rot * b.Center;
                uint space = carrier.SpaceId;
                Rigidbody root = pt.GetComponentInParent<Rigidbody>();
                Vector3 v = root != null ? root.GetPointVelocity(pos) : Vector3.zero;
                ServiceLocator.GetService<ISpacesServerProvider>().SetWorldObjectPhysics(ship, space, new LocalPhysicsState { Position = pos, Rotation = rot, Velocity = v });
                ship.NotifyRepositioned();
                Log.Msg($"[DEV] ship {ship.Id} brought over pad {pad.Id} of ship {carrier.Id} ({h} m, yaw {yaw})");
            });
            _harness.Command("zone", a =>
            {
                // zone <ship> <carrier>: the ship's collider box and where its corners fall in the carrier's first pad zone
                var ships = Svc.ShipsServer;
                TrackedShipServer ship = ships.GetShip(uint.Parse(a[1])), carrier = ships.GetShip(uint.Parse(a[2]));
                var shapes = Tractor.Shapes(ship);
                Bounds box = Tractor.LocalBounds(shapes);
                var hb = ship.GetHullCoordBounds();
                Log.Msg($"[DEV] ship {ship.Id}: {shapes.Count} boxes, local bounds {box.center:F2} size {box.size:F2}; hull voxels {hb.Center} ext {hb.Extents}");
                var pad = Pads.PadsOf(carrier).First();
                var (pp, pr) = Pads.Frame(pad);
                Transform pt = carrier.RigidbodyObject.transform, ct = ship.RigidbodyObject.transform;
                Quaternion inv = Quaternion.Inverse(pt.rotation * pr);
                foreach (Vector3 c in Tractor.Corners(box))
                    Log.Msg($"[DEV]   corner {c:F1} -> pad {inv * (ct.TransformPoint(c) - pt.TransformPoint(pp)):F1}");
                var far = shapes.OrderByDescending(x => x.Center.magnitude).Take(3);
                foreach (var f in far) Log.Msg($"[DEV]   far box {f.Center:F1} half {f.Half:F2}");
            });
            _harness.Command("grav", a =>
            {
                // grav <ship>: gravity at the ship and how its up axis compares
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                Vector3 g = ServiceLocator.GetService<WorldProvision.Interface.Services.IWorldEnvironmentServerProvider>().GetGravityAtPosition(ship.SpaceId, ship.Position);
                Transform t = ship.RigidbodyObject.transform;
                Log.Msg($"[DEV] ship {ship.Id}: gravity {g:F2}, up {t.up:F2}, up·(-g) {Vector3.Dot(t.up, -g.normalized):F2}");
            });
            _harness.Command("upright", a =>
            {
                // upright <ship> <h>: turns the ship so that its up is against gravity (same heading) and lifts it h m
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                Vector3 g = ServiceLocator.GetService<WorldProvision.Interface.Services.IWorldEnvironmentServerProvider>().GetGravityAtPosition(ship.SpaceId, ship.Position);
                Vector3 up = -g.normalized;
                Transform t = ship.RigidbodyObject.transform;
                Vector3 fwd = Vector3.ProjectOnPlane(t.forward, up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(t.up, up);
                Quaternion rot = Quaternion.LookRotation(fwd.normalized, up);
                ServiceLocator.GetService<ISpacesServerProvider>().SetWorldObjectPhysics(ship, ship.SpaceId,
                    new LocalPhysicsState { Position = ship.Position + up * Fl(a[2]), Rotation = rot, Velocity = Vector3.zero });
                ship.NotifyRepositioned();
                Log.Msg($"[DEV] ship {ship.Id} upright, {a[2]} m up");
            });
            _harness.Command("unpause", a =>
            {
                foreach (var b in Object.FindObjectsByType<UI.Pause.ContinueButton>(FindObjectsInactive.Include, FindObjectsSortMode.None)) b.Unpause();
            });
            _harness.Command("save", a =>
            {
                ServiceLocator.GetService<Saving.Interface.Services.IGameSaver>().SaveGame(new Saving.Interface.Model.SaveGameParams { IsAutomatic = false });
                Log.Msg("[DEV] saving");
            });
            _harness.Command("cols", a =>
            {
                // cols <ship>: the ship's exterior colliders, enabled or not (Ship Performance Fix turns corners off)
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var all = ship.RigidbodyObject.GetComponentsInChildren<Collider>(true);
                var rb = ship.RigidbodyObject.GetComponentInParent<Rigidbody>();
                Log.Msg($"[DEV] ship {ship.Id}: {all.Length} colliders, {all.Count(c => !c.enabled)} disabled ({all.OfType<MeshCollider>().Count(c => !c.enabled)} mesh), body {rb?.name}, inertia {rb?.inertiaTensor:F1} auto {rb?.automaticInertiaTensor}, mass {rb?.mass:F0}");
            });
            _harness.Command("later", a =>
            {
                // later <seconds> <command...>: runs a command after a delay (e.g. land a ship just brought)
                float delay = Fl(a[1]);
                string line = string.Join(" ", a.Skip(2));
                MelonLoader.MelonCoroutines.Start(Later(delay, line));
            });
            _harness.Command("pilot", a =>
            {
                // pilot <ship>: the local player takes the ship's pilot seat
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var seat = ship.StatefulParts.FirstOrDefault(p => p.State is Ships.Interface.Model.Parts.State.PilotSeatState || p.State is Ships.Interface.Model.Parts.State.PilotSeatAdvState);
                var service = Object.FindObjectsByType<Managers.Server.Services.ShipInteractionService>(FindObjectsSortMode.None).First();
                HarmonyLib.Traverse.Create(service).Method("PlayerStartPiloting", Svc.PlayersServer.ActivePlayers.First(), ship, seat.Id).GetValue();
                Log.Msg($"[DEV] piloting ship {ship.Id} from seat {seat.Id}");
            });
            _harness.Command("parts", a =>
            {
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                Log.Msg($"[DEV] ship {ship.Id} parts: " + string.Join(", ", ship.StatefulParts.Select(p => $"{p.Id}:{p.Settings?.id}:{p.State?.GetType().Name}")));
            });
            _harness.Command("undock", a =>
            {
                // undock <ship>: lets go of the ship's docked docking doors (both sides)
                var ships = Svc.ShipsServer;
                TrackedShipServer ship = ships.GetShip(uint.Parse(a[1]));
                foreach (var door in ship.GetAllStatefulParts<Ships.Interface.Model.Parts.State.DockingDoorState>().ToList())
                {
                    var st = (Ships.Interface.Model.Parts.State.DockingDoorState)door.State;
                    if (!st.IsDocked) continue;
                    ships.GetShip(st.DockedConnectedPart.ShipId)?.UndockPart(st.DockedConnectedPart.PartId);
                    ship.UndockPart(door.Id);
                    Log.Msg($"[DEV] ship {ship.Id} door {door.Id} undocked from ship {st.DockedConnectedPart.ShipId}");
                }
            });
            _harness.Command("press", a => ParkingTractorBeam.Net.TractorNet.SendToServer(ParkingTractorBeam.Net.Op.Request, null));
            _harness.Command("zoneset", a =>
            {
                // zoneset <carrier> bx by bz sx sy sz: the zone of the carrier's first pad (pad frame), as the menu would
                TrackedShipServer carrier = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var pad = Pads.PadsOf(carrier).First();
                var cfg = PadConfigs.Get(carrier.Id, pad).With(new Zone { Bottom = new Vector3(Fl(a[2]), Fl(a[3]), Fl(a[4])), Size = new Vector3(Fl(a[5]), Fl(a[6]), Fl(a[7])) }).Clamped(Pads.Spec(pad));
                PadConfigs.Set(new Ships.Interface.Model.Parts.PartContext(carrier.Id, pad.Id), cfg);
                Log.Msg($"[DEV] zone of pad {pad.Id}: bottom {cfg.Zone.Bottom} size {cfg.Zone.Size}");
            });
            _harness.Command("bringzone", a =>
            {
                // bringzone <ship> <carrier> [yaw] [speed]: puts the ship in the middle of the carrier's first pad zone, at rest
                // or moving along the pad's forward at that speed (m/s)
                var ships = Svc.ShipsServer;
                TrackedShipServer ship = ships.GetShip(uint.Parse(a[1])), carrier = ships.GetShip(uint.Parse(a[2]));
                var pad = Pads.PadsOf(carrier).First();
                Zone z = Pads.ZoneOf(carrier.Id, pad);
                var (pp, pr) = Pads.Frame(pad);
                Transform pt = carrier.RigidbodyObject.transform;
                var b = ship.GetHullCoordBounds();
                Quaternion rot = pt.rotation * pr * Quaternion.Euler(0f, a.Length > 3 ? Fl(a[3]) : 20f, 0f);
                Vector3 pos = pt.TransformPoint(pp + pr * z.Center) - rot * b.Center;
                Vector3 vel = a.Length > 4 ? pt.rotation * pr * Vector3.forward * Fl(a[4]) : Vector3.zero;
                ServiceLocator.GetService<ISpacesServerProvider>().SetWorldObjectPhysics(ship, carrier.SpaceId, new LocalPhysicsState { Position = pos, Rotation = rot, Velocity = vel });
                ship.NotifyRepositioned();
                Log.Msg($"[DEV] ship {ship.Id} in the zone of pad {pad.Id} of ship {carrier.Id}");
            });
            _harness.Command("usepad", a =>
            {
                // usepad <carrier>: the local player opens the menu of the carrier's first pad (as with E)
                TrackedShipServer ship = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var pad = Pads.PadsOf(ship).First();
                var service = Object.FindObjectsByType<Managers.Server.Services.ShipInteractionService>(FindObjectsSortMode.None).First();
                HarmonyLib.Traverse.Create(service).Method("PlayerStartInteracting", Svc.PlayersServer.ActivePlayers.First(), ship, pad).GetValue();
                Log.Msg($"[DEV] using pad {pad.Id}");
            });
            _harness.Command("prefab", a =>
            {
                // prefab <settings id>: the hierarchy of a part's prefab, with components
                var settings = Resources.FindObjectsOfTypeAll<PartSettings>().First(p => p.id == ushort.Parse(a[1]));
                void Dump(Transform t, string indent)
                {
                    Log.Msg($"[DEV] {indent}{t.name} [{(t.gameObject.activeSelf ? "on" : "off")}] {t.localPosition:F2} : {string.Join(", ", t.GetComponents<Component>().Select(c => c.GetType().Name))}");
                    foreach (Transform c in t) Dump(c, indent + "  ");
                }
                Dump(settings.part.transform, "");
            });
            _harness.Command("uiclick", a =>
            {
                // uiclick <n>: clicks the n-th button of the pad menu (0 = first row's -5)
                var canvas = GameObject.Find("ParkingTractorBeam.PadMenu");
                var buttons = canvas.GetComponentsInChildren<UnityEngine.UI.Button>();
                buttons[int.Parse(a[1])].onClick.Invoke();
                Log.Msg($"[DEV] clicked button {a[1]} of {buttons.Length}");
            });
            _harness.Command("state", a =>
            {
                Svc.PlayersClient.TryGetLocalPlayerState(out Players.Interface.Model.PlayerState st);
                Log.Msg($"[DEV] player {st.Type} {st.EntityId}/{st.PartId}, menu {(GameObject.Find("ParkingTractorBeam.PadMenu")?.activeSelf == true ? "open" : "closed")}, cursor {Cursor.lockState}");
            });
            _harness.Command("mat", a =>
            {
                // mat: the borrowed spark material's shader, its keywords and blend settings
                Material m = ParkingTractorBeam.Visual.Assets.Spark;
                Shader sh = m.shader;
                var props = Enumerable.Range(0, sh.GetPropertyCount()).Select(i => sh.GetPropertyName(i) + (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Float || sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Range ? "=" + m.GetFloat(sh.GetPropertyName(i)) : ""));
                Log.Msg($"[DEV] spark: shader {sh.name}, queue {m.renderQueue}, keywords {string.Join(" ", m.shaderKeywords)}, props {string.Join(", ", props)}");
            });
            _harness.Command("parkset", a =>
            {
                // parkset <carrier> x y z heading: the parking spot of the carrier's first pad (pad frame), as the menu would
                TrackedShipServer carrier = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var pad = Pads.PadsOf(carrier).First();
                var cfg = PadConfigs.Get(carrier.Id, pad).With(new Vector3(Fl(a[2]), Fl(a[3]), Fl(a[4])), int.Parse(a[5])).Clamped(Pads.Spec(pad));
                PadConfigs.Set(new Ships.Interface.Model.Parts.PartContext(carrier.Id, pad.Id), cfg);
                Log.Msg($"[DEV] parking spot of pad {pad.Id}: {cfg.Park} heading {cfg.Heading}");
            });
            _harness.Command("tab", a =>
            {
                // tab <0|1>: the pad menu's zone or parking tab
                HarmonyLib.Traverse.Create(typeof(ParkingTractorBeam.Client.PadMenu)).Method("SetTab", new[] { HarmonyLib.AccessTools.Inner(typeof(ParkingTractorBeam.Client.PadMenu), "Tab") }, new object[] { System.Enum.ToObject(HarmonyLib.AccessTools.Inner(typeof(ParkingTractorBeam.Client.PadMenu), "Tab"), int.Parse(a[1])) }).GetValue();
            });
            _harness.Command("orbit", a =>
            {
                // orbit yaw pitch zoom: the pad menu's preview camera
                var t = HarmonyLib.Traverse.Create(typeof(ParkingTractorBeam.Client.PadMenu));
                t.Field("_yaw").SetValue(Fl(a[1]));
                t.Field("_pitch").SetValue(Fl(a[2]));
                t.Field("_zoom").SetValue(Fl(a[3]));
            });
            _harness.Command("land", a => Log.Msg($"[DEV] land {a[1]}: {Tractor.Request(uint.Parse(a[1]))}"));
            _harness.Command("launch", a => Log.Msg($"[DEV] launch {a[1]}: {Tractor.Request(uint.Parse(a[1]))}"));
            _harness.Command("docks", a =>
            {
                foreach (DockRecord r in Docks.Server.Values)
                    Log.Msg($"[DEV] dock {r.Child} on {r.Parent}/{r.Pad} at {r.LocalPos:F3} {r.LocalRot.eulerAngles:F1}");
                foreach (TractorOp op in Tractor.Ops.Values)
                    Log.Msg($"[DEV] beam {op.Child} -> {op.Parent}/{op.Pad} {(op.Launch ? "launch" : "land")} {op.T:F1}/{op.Total:F1} s");
                foreach (var kv in Docks.Known)
                    Log.Msg($"[DEV] client knows {kv.Key} on {kv.Value.Parent}/{kv.Value.Pad}");
            });
            _harness.Command("where", a =>
            {
                TrackedShipServer s = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                Transform t = s.RigidbodyObject.transform;
                var rb = t.GetComponent<Rigidbody>();
                var joints = Svc.JointsServer;
                string joint = joints.TryGetShipJoint(s.Id, out TrackedShipJoint j) ? $"parent {j.Parent?.ShipId}, children {j.Children?.Count ?? 0}" : "no joint";
                Log.Msg($"[DEV] ship {s.Id}: world {t.position:F3} {t.rotation.eulerAngles:F1}, state {s.Position:F3} space {s.SpaceId}, rb {(rb == null ? "none" : $"vel {rb.linearVelocity:F2} kin {rb.isKinematic}")}, transform parent {t.parent?.name}, {joint}");
                if (Svc.ShipsClient.TryGetTrackedShip(s.Id, out TrackedShipClient c))
                    Log.Msg($"[DEV] client ship {s.Id}: state {c.Position:F3} space {c.SpaceId}, visual {(c.IsVisualLoaded ? c.VisualObject.transform.position.ToString("F3") : "-")}");
            });
            _harness.Command("tp", a =>
            {
                TrackedShipServer s = Svc.ShipsServer.GetShip(uint.Parse(a[1]));
                var player = Svc.PlayersServer.ActivePlayers.First();
                ServiceLocator.GetService<ISpacesServerProvider>().SetWorldObjectPhysics(player, s.RelativeSpaceId,
                    new LocalPhysicsState { Position = new Vector3(Fl(a[2]), Fl(a[3]), Fl(a[4])), Rotation = Quaternion.identity, Velocity = Vector3.zero });
                Log.Msg($"[DEV] player to ship {s.Id} {a[2]} {a[3]} {a[4]}");
            });
            _harness.Command("look", a =>
            {
                Svc.ShipsClient.TryGetTrackedShip(uint.Parse(a[1]), out TrackedShipClient tracked);
                Vector3 target = tracked.VisualObject.transform.TransformPoint(new Vector3(Fl(a[2]), Fl(a[3]), Fl(a[4])));
                var look = Object.FindObjectsByType<PlayerLookControls>(FindObjectsSortMode.None).First();
                Camera cam = Camera.main;
                look.Rotation = Quaternion.LookRotation(target - cam.transform.position, look.Rotation * Vector3.up);
            });
        }

        private static void Place(uint shipId, ushort settingsId, float x, float z, bool lowest)
        {
            TrackedShipServer ship = Svc.ShipsServer.GetShip(shipId);
            var b = ship.GetHullCoordBounds();
            if (float.IsNaN(x)) x = Mathf.Round(b.Center.x);
            if (float.IsNaN(z)) z = Mathf.Round(b.Center.z);
            Transform t = ship.RigidbodyObject.transform;
            Vector3 from = t.TransformPoint(new Vector3(x, b.UpperCoord.y + 5f, z));
            var rb = t.GetComponentInParent<Rigidbody>();
            var all = new RaycastHit[64];
            int n = t.gameObject.scene.GetPhysicsScene().Raycast(from, -t.up, all, b.Extents.y * 2f + 10f, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            var hits = all.Take(n).Where(h => h.collider.attachedRigidbody == rb && Vector3.Dot(h.normal, t.up) > 0.7f).OrderBy(h => h.distance).ToList();
            if (hits.Count == 0)
            {
                Log.Msg("[DEV] place: no upward hull face under that point");
                return;
            }
            RaycastHit hit = lowest ? hits.Last() : hits.First();
            Vector3 local = t.InverseTransformPoint(hit.point);
            var settings = (PartSettings)new Ships.Interface.Services.PartSettingsProvider().GetPartSettingById(settingsId);
            ushort id = ship.AddStatefulPart(settings, new FloatingPartPlacement(local, PartOrientation.Forward, PartOrientation.Up));
            Log.Msg($"[DEV] part {settingsId} id {id} on ship {ship.Id} at {local:F3} (hit {hit.collider.name})");
        }

        private static System.Collections.IEnumerator Later(float delay, string line)
        {
            float until = Time.realtimeSinceStartup + delay;
            while (Time.realtimeSinceStartup < until) yield return null;
            Log.Msg($"[DEV] later: {line}");
            _harness.Run(line);
        }

        private static void DevSceneLoaded(int buildIndex, string sceneName) => _harness?.SceneLoaded(buildIndex, sceneName);

        private static void DevUpdate() => _harness?.Update();
    }
}
#endif
