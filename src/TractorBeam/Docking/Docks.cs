using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Managers.Interface;
using Saving.Services;
using Ships;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Services;
using Ships.Joints;
using Ships.Services;
using TractorBeam.Core;
using TractorBeam.Net;
using UnityEngine;
using WorldTracking.Interface.Model.Spaces;

namespace TractorBeam.Docking
{
    /// <summary>A ship set down on a tractor pad: the host's record of it.</summary>
    internal sealed class DockRecord
    {
        public uint Child, Parent;
        public ushort Pad;
        /// <summary>The child's pose in the parent ship's frame once landed.</summary>
        public Vector3 LocalPos;
        public Quaternion LocalRot = Quaternion.identity;
        /// <summary>Where the beam caught it (parent frame): the way out when it leaves.</summary>
        public bool HasExit;
        public Vector3 ExitPos;
        public Quaternion ExitRot = Quaternion.identity;
    }

    /// <summary>
    /// A landed ship is joined to the carrier with the game's own joint system, the one docking doors and rotors use:
    /// its colliders move under the carrier's rigidbody (one compound body, so the two hulls never collide, and nothing
    /// jitters), it lives in the carrier's relative space (synced and saved by the game), and mass, thrust and every
    /// "connected ships" query include it.
    ///
    /// The joint tree is built from docking doors and from <c>GetShipRotorJoints</c>. Each landed ship adds a fake rotor
    /// entry there, with a part id no real part uses, and <c>BuildRotorConnection</c> turns that entry into a
    /// <see cref="TractorJoint"/> holding the landed pose.
    ///
    /// The host keeps the records (and saves them next to world.json). Clients do not need them: a ship in another ship's
    /// relative space is always jointed to it, so they join any such ship that no native joint holds.
    /// </summary>
    internal static class Docks
    {
        private const string FileName = "tractorbeam-docks.txt";

        public static readonly Dictionary<uint, DockRecord> Server = new Dictionary<uint, DockRecord>();

        /// <summary>Docked ships as the host announced them (child -> parent, pad): for the HUD and the beam.</summary>
        public static readonly Dictionary<uint, (uint Parent, ushort Pad)> Known = new Dictionary<uint, (uint, ushort)>();

        // fake rotor part ids, per (parent, child) pair, both ways
        private static readonly Dictionary<(uint, uint), ushort> FakeIds = new Dictionary<(uint, uint), ushort>();
        private static readonly Dictionary<PartContext, (uint Parent, uint Child)> Fakes = new Dictionary<PartContext, (uint, uint)>();

        // client: child -> parent of the ships we join because they sit in another ship's relative space
        private static readonly Dictionary<uint, uint> ClientDerived = new Dictionary<uint, uint>();

        private static readonly AccessTools.FieldRef<ShipsJointsChangeProvider, HashSet<uint>> ChangedRef =
            AccessTools.FieldRefAccess<ShipsJointsChangeProvider, HashSet<uint>>("_changedShips");
        private static readonly AccessTools.FieldRef<ShipsJointsChangeProvider, Dictionary<uint, List<RotorJointData>>> RotorsRef =
            AccessTools.FieldRefAccess<ShipsJointsChangeProvider, Dictionary<uint, List<RotorJointData>>>("_shipsRotorsCache");

        // carriers whose inertia is recomputed one tick after a ship joined or left them (once the colliders moved)
        private static HashSet<uint> _inertiaNext = new HashSet<uint>(), _inertiaNow = new HashSet<uint>();

        private static bool _markAllOnNextTick;
        private static readonly HashSet<uint> _orphanSuspects = new HashSet<uint>();
        private static float _nextOrphanCheck;

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ShipsJointsChangeProvider), nameof(ShipsJointsChangeProvider.GetShipRotorJoints)),
                postfix: new HarmonyMethod(typeof(Docks), nameof(RotorJointsPostfix)));
            h.Patch(AccessTools.Method(typeof(ShipsJointsTreeTracker), "BuildRotorConnection"),
                prefix: new HarmonyMethod(typeof(Docks), nameof(BuildRotorConnectionPrefix)));
            h.Patch(AccessTools.Method(typeof(GameSaver), "SaveGame", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(Docks), nameof(AfterSave)));
            h.Patch(AccessTools.Method(typeof(GameLoader), nameof(GameLoader.LoadState)),
                postfix: new HarmonyMethod(typeof(Docks), nameof(AfterLoad)));
            TractorNet.OnClient(Op.Docks, ClientOnDocks);
            TractorNet.OnClientHello += conn => TractorNet.SendTo(conn, Op.Docks, WriteDocks);
        }

        public static void Reset()
        {
            Known.Clear();
            ClientDerived.Clear();
            FakeIds.Clear();
            Fakes.Clear();
        }

        /// <summary>Server state lives as long as the world (reloaded with the next one).</summary>
        public static void ResetServer()
        {
            Server.Clear();
            _orphanSuspects.Clear();
        }

        public static bool IsDockedChild(uint shipId) => Server.ContainsKey(shipId);

        public static DockRecord OnPad(uint parent, ushort pad) => Server.Values.FirstOrDefault(r => r.Parent == parent && r.Pad == pad);

        // ------------------------------------------------------------------ records (host)

        public static void Add(DockRecord r)
        {
            Server[r.Child] = r;
            MarkServer(r.Child, r.Parent);
            _inertiaNext.Add(r.Parent);
            Broadcast();
            TractorMod.Log.Msg($"Ship {r.Child} landed on pad {r.Pad} of ship {r.Parent}.");
        }

        public static void Remove(uint child)
        {
            if (!Server.TryGetValue(child, out DockRecord r)) return;
            Server.Remove(child);
            MarkServer(r.Child, r.Parent);
            _inertiaNext.Add(r.Parent);
            Broadcast();
        }

        private static void MarkServer(params uint[] ships)
        {
            var changes = Svc.JointChangesServer;
            if (changes == null) return;
            foreach (uint s in ships) ChangedRef(changes).Add(s);
        }

        private static void MarkClient(params uint[] ships)
        {
            var changes = Svc.JointChangesClient;
            if (changes == null) return;
            foreach (uint s in ships) ChangedRef(changes).Add(s);
        }

        private static void Broadcast() => TractorNet.SendToAll(Op.Docks, WriteDocks);

        private static void WriteDocks(BinaryWriter w)
        {
            w.Write(Server.Count);
            foreach (DockRecord r in Server.Values)
            {
                w.Write(r.Child);
                w.Write(r.Parent);
                w.Write(r.Pad);
            }
        }

        private static void ClientOnDocks(BinaryReader r)
        {
            Known.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                Known[r.ReadUInt32()] = (r.ReadUInt32(), r.ReadUInt16());
        }

        /// <summary>Host, before the joints update of each tick: records whose ships or pad are gone, and orphans.</summary>
        public static void ServerTick()
        {
            var ships = Svc.ShipsServer;
            if (ships == null) return;
            foreach (uint id in _inertiaNow) RecomputeInertia(ships.GetShip(id));
            _inertiaNow.Clear();
            (_inertiaNow, _inertiaNext) = (_inertiaNext, _inertiaNow);
            if (_markAllOnNextTick)
            {
                _markAllOnNextTick = false;
                foreach (DockRecord r in Server.Values) MarkServer(r.Child, r.Parent);
            }
            List<uint> gone = null;
            foreach (DockRecord r in Server.Values)
            {
                TrackedShipServer child = ships.GetShip(r.Child), parent = ships.GetShip(r.Parent);
                if (child == null || parent == null || child.IsShipDestroyed || parent.IsShipDestroyed
                    || !parent.TryGetStatefulPart(r.Pad, out StatefulPart pad) || !Pads.IsPad(pad))
                    (gone ??= new List<uint>()).Add(r.Child);
            }
            if (gone != null)
                foreach (uint c in gone)
                {
                    TractorMod.Log.Msg($"Ship {c} released: its carrier or pad is gone.");
                    Remove(c);
                }
            if (Time.realtimeSinceStartup >= _nextOrphanCheck)
            {
                _nextOrphanCheck = Time.realtimeSinceStartup + 2f;
                AdoptOrphans(ships);
            }
        }

        /// <summary>
        /// The carrier's body gained or lost the landed ship's colliders. The game lets Unity recompute the inertia when
        /// its own colliders change (between these two calls); Ship Performance Fix freezes the inertia of big ships and
        /// completes it in the second call. Without this, the carrier would keep turning as if the ship were still there.
        /// </summary>
        private static void RecomputeInertia(TrackedShipServer carrier)
        {
            if (carrier == null || !carrier.IsRigidbodyLoaded || carrier.RigidbodyObject == null) return;
            Rigidbody root = carrier.RigidbodyObject.GetComponentInParent<Rigidbody>();
            var body = root != null ? root.GetComponent<Ships.Physics.ShipRigidbody>() : null;
            if (body == null) return;
            body.DisableAutomaticCalculation();
            body.EnableAutomaticCalculation();
        }

        /// <summary>
        /// A ship in another ship's relative space that no joint holds (a world saved with landed ships but without our
        /// file, or a record dropped): adopt it on the nearest pad, or let the game take it out of that space.
        /// </summary>
        private static void AdoptOrphans(ShipsServerTracker ships)
        {
            var joints = Svc.JointsServer;
            var spaces = Svc.SpacesServer;
            if (joints == null || spaces == null) return;
            var suspects = new HashSet<uint>(_orphanSuspects);
            _orphanSuspects.Clear();
            foreach (TrackedShipServer ship in ships.Ships)
            {
                if (Server.ContainsKey(ship.Id) || Tractor.IsBusy(ship.Id)) continue;
                if (!(spaces.GetSpace(ship.SpaceId) is ShipRelativeSpace rel)) continue;
                if (joints.TryGetShipJoint(ship.Id, out TrackedShipJoint j) && j.Parent != null) continue;
                // held by a rotor or a docking door: the game joins it (maybe not yet, just after loading)
                if (NativelyJoined(Svc.JointChangesServer, ships, rel.ShipId, ship.Id)) continue;
                // and only once it has stayed that way for a whole check
                if (!suspects.Contains(ship.Id))
                {
                    _orphanSuspects.Add(ship.Id);
                    continue;
                }
                _orphanSuspects.Add(ship.Id);
                TrackedShipServer parent = ships.GetShip(rel.ShipId);
                StatefulPart pad = parent == null ? null : Pads.PadsOf(parent)
                    .Where(p => OnPad(parent.Id, p.Id) == null)
                    .OrderBy(p => (Pads.Frame(p).pos - ship.Position).sqrMagnitude).FirstOrDefault();
                if (pad != null)
                {
                    TractorMod.Log.Msg($"Ship {ship.Id} sits in ship {parent.Id} with no joint: kept on its pad {pad.Id}.");
                    Add(new DockRecord { Child = ship.Id, Parent = parent.Id, Pad = pad.Id, LocalPos = ship.Position, LocalRot = ship.Rotation });
                }
                else
                {
                    MarkServer(ship.Id);
                }
            }
        }

        /// <summary>Client, every frame: ships that entered or left another ship's relative space change the joint tree.</summary>
        public static void ClientTick()
        {
            var ships = Svc.ShipsClient;
            var spaces = Svc.SpacesClient;
            if (ships == null || spaces == null) return;
            foreach (TrackedShipClient ship in ships.Ships)
            {
                uint parent = spaces.TryGetSpace(ship.SpaceId, out var space) && space is ShipRelativeSpace rel ? rel.ShipId : 0;
                ClientDerived.TryGetValue(ship.Id, out uint was);
                if (parent == was) continue;
                if (parent == 0) ClientDerived.Remove(ship.Id);
                else ClientDerived[ship.Id] = parent;
                MarkClient(ship.Id);
                if (was != 0) MarkClient(was);
                if (parent != 0) MarkClient(parent);
            }
            if (ClientDerived.Count > 0)
            {
                List<uint> gone = null;
                foreach (uint id in ClientDerived.Keys)
                    if (!ships.TryGetTrackedShip(id, out _))
                        (gone ??= new List<uint>()).Add(id);
                if (gone != null)
                    foreach (uint id in gone) ClientDerived.Remove(id);
            }
        }

        // ------------------------------------------------------------------ joint tree

        private static ushort FakeId(uint parent, uint child)
        {
            if (FakeIds.TryGetValue((parent, child), out ushort id)) return id;
            id = (ushort)(ushort.MaxValue - FakeIds.Count(kv => kv.Key.Item1 == parent));
            FakeIds[(parent, child)] = id;
            Fakes[new PartContext(parent, id)] = (parent, child);
            return id;
        }

        private static void RotorJointsPostfix(ShipsJointsChangeProvider __instance, uint shipId, ref IReadOnlyCollection<RotorJointData> __result)
        {
            List<(uint Parent, uint Child)> pairs = null;
            if (__instance is ShipsJointsChangeProviderServer)
            {
                foreach (DockRecord r in Server.Values)
                    if (r.Child == shipId || r.Parent == shipId)
                        (pairs ??= new List<(uint, uint)>()).Add((r.Parent, r.Child));
            }
            else
            {
                foreach (var kv in ClientDerived)
                    if ((kv.Key == shipId || kv.Value == shipId) && !NativelyJoined(__instance, kv.Value, kv.Key))
                        (pairs ??= new List<(uint, uint)>()).Add((kv.Value, kv.Key));
            }
            if (pairs == null) return;
            var list = new List<RotorJointData>(__result);
            foreach (var (parent, child) in pairs)
                list.Add(new RotorJointData { RotorPart = new PartContext(parent, FakeId(parent, child)), OtherShipId = child });
            __result = list;
        }

        private static bool NativelyJoined(ShipsJointsChangeProvider changes, uint parent, uint child) =>
            NativelyJoined(changes, Svc.ShipsClient, parent, child);

        /// <summary>The two ships share a rotor or a docked docking door (the game joins those itself).</summary>
        private static bool NativelyJoined(ShipsJointsChangeProvider changes, IShipsProvider ships, uint parent, uint child)
        {
            if (changes != null && RotorsRef(changes).TryGetValue(child, out List<RotorJointData> rotors))
                foreach (RotorJointData d in rotors)
                    if (d.OtherShipId == parent || d.RotorPart.ShipId == parent)
                        return true;
            if (ships == null) return false;
            foreach (var (a, b) in new[] { (child, parent), (parent, child) })
                if (ships.TryGetShip(a, out Ships.Interface.Model.State.IShipStateRead s))
                    foreach (StatefulPart door in s.GetAllStatefulParts<DockingDoorState>())
                        if (door.State is DockingDoorState d && d.IsDocked && d.DockedConnectedPart.ShipId == b)
                            return true;
            return false;
        }

        private static bool BuildRotorConnectionPrefix(ShipsJointsTreeTracker __instance, PartContext rotorPartContext, ref RotorJointConnection __result)
        {
            if (!Fakes.TryGetValue(rotorPartContext, out var pair)) return true;
            __result = new TractorJoint(pair.Parent, pair.Child, __instance is ShipsJointsTreeTrackerServer) { RotorPart = rotorPartContext };
            return false;
        }

        /// <summary>The child's pose in the parent's frame: the record on the host, the synced relative state on clients.</summary>
        public static (Vector3, Quaternion) LocalPose(uint parent, uint child, bool server)
        {
            if (server)
                return Server.TryGetValue(child, out DockRecord r) ? (r.LocalPos, r.LocalRot) : (Vector3.zero, Quaternion.identity);
            var ships = Svc.ShipsClient;
            if (ships != null && ships.TryGetTrackedShip(child, out TrackedShipClient c) && ships.TryGetTrackedShip(parent, out TrackedShipClient p) && c.SpaceId == p.RelativeSpaceId)
                return (c.Position, c.Rotation);
            return (Vector3.zero, Quaternion.identity);
        }

        // ------------------------------------------------------------------ saving

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static void AfterSave(string worldDirectoryFullName)
        {
            try
            {
                string path = Path.Combine(worldDirectoryFullName, FileName);
                if (Server.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                var lines = new List<string> { "# Tractor Beam docks v1: child parent pad  x y z  qx qy qz qw  exit(0|1) x y z qx qy qz qw" };
                foreach (DockRecord r in Server.Values)
                    lines.Add(string.Join(" ", r.Child, r.Parent, r.Pad,
                        F(r.LocalPos.x), F(r.LocalPos.y), F(r.LocalPos.z), F(r.LocalRot.x), F(r.LocalRot.y), F(r.LocalRot.z), F(r.LocalRot.w),
                        r.HasExit ? 1 : 0, F(r.ExitPos.x), F(r.ExitPos.y), F(r.ExitPos.z), F(r.ExitRot.x), F(r.ExitRot.y), F(r.ExitRot.z), F(r.ExitRot.w)));
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Saving docks failed: {ex.Message}");
            }
        }

        private static void AfterLoad()
        {
            Server.Clear();
            try
            {
                string world = MainGameSceneLoadData.SaveFileFullName;
                string dir = string.IsNullOrEmpty(world) ? null : Path.GetDirectoryName(world);
                string path = dir == null ? null : Path.Combine(dir, FileName);
                if (path == null || !File.Exists(path)) return;
                float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
                    string[] p = line.Split(' ');
                    if (p.Length < 18) continue;
                    var r = new DockRecord
                    {
                        Child = uint.Parse(p[0]), Parent = uint.Parse(p[1]), Pad = ushort.Parse(p[2]),
                        LocalPos = new Vector3(P(p[3]), P(p[4]), P(p[5])),
                        LocalRot = new Quaternion(P(p[6]), P(p[7]), P(p[8]), P(p[9])).normalized,
                        HasExit = p[10] == "1",
                        ExitPos = new Vector3(P(p[11]), P(p[12]), P(p[13])),
                        ExitRot = new Quaternion(P(p[14]), P(p[15]), P(p[16]), P(p[17])).normalized,
                    };
                    Server[r.Child] = r;
                }
                _markAllOnNextTick = true;
                if (Server.Count > 0) TractorMod.Log.Msg($"Loaded {Server.Count} landed ships.");
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Loading docks failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The joint of a landed ship. It derives from the rotor joint only because <c>BuildRotorConnection</c> returns that
    /// type; it re-implements <see cref="IJointConnection"/>, so the game's calls through the interface land here.
    /// </summary>
    internal sealed class TractorJoint : RotorJointConnection, IJointConnection
    {
        public readonly uint Parent, Child;
        private readonly bool _server;

        public TractorJoint(uint parent, uint child, bool server)
        {
            Parent = parent;
            Child = child;
            _server = server;
        }

        public new (Vector3, Quaternion) CalculateTransform(IShipsProvider ships)
        {
            var (p, q) = Docks.LocalPose(Parent, Child, _server);
            // The tree builder sets this flag on each visit of a node and the child's visit comes last: it ends up true
            // when the carrier (ship A, the rotor side) is the parent. That is how the game's joints read it too.
            if (IsDirectionReversed) return (p, q);
            // the carrier hangs under the landed ship (it weighs more): the inverse pose
            Quaternion inv = Quaternion.Inverse(q);
            return (inv * -p, inv);
        }

        public override string ToString() => $"TractorJoint {Parent} -> {Child}";
    }
}
