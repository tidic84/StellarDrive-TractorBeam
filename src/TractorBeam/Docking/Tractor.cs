using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet.Connection;
using HarmonyLib;
using Managers.Interface.Model;
using Players.Interface.Model;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Joints;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.State;
using Ships.Physics;
using TractorBeam.Core;
using TractorBeam.Net;
using UnityEngine;

namespace TractorBeam.Docking
{
    /// <summary>Answers to a landing or take-off request (sent back to the pilot).</summary>
    internal enum Verdict : byte
    {
        Landing = 1,
        Launching = 2,
        NotPiloting = 10,
        Busy = 11,
        NoZone = 12,
        NotInside = 13,
        TooFast = 14,
        Spinning = 15,
        TooBig = 16,
        PadBusy = 17,
        Blocked = 18,
        Jointed = 19,
        Anchored = 20,
        Heavier = 21,
        NoRoom = 22,
    }

    /// <summary>A pose in the carrier's frame.</summary>
    internal struct Pose
    {
        public Vector3 P;
        public Quaternion R;

        public Pose(Vector3 p, Quaternion r)
        {
            P = p;
            R = r;
        }
    }

    /// <summary>One leg of a beam animation, in the carrier's frame.</summary>
    internal struct Leg
    {
        public enum Kind { Brake, Smooth, SetDown, Path }

        public Kind Ease;
        public Pose A, B;
        public Vector3 V0;
        public float Duration;
        /// <summary>Path: the corners from A to B (both included), followed at an eased speed.</summary>
        public Vector3[] Points;

        public float Length
        {
            get
            {
                if (Ease != Kind.Path || Points == null) return Vector3.Distance(A.P, B.P);
                float l = 0f;
                for (int i = 1; i < Points.Length; i++) l += Vector3.Distance(Points[i - 1], Points[i]);
                return l;
            }
        }

        private Vector3 Along(float d)
        {
            for (int i = 1; i < Points.Length; i++)
            {
                float seg = Vector3.Distance(Points[i - 1], Points[i]);
                if (d <= seg || i == Points.Length - 1) return Vector3.Lerp(Points[i - 1], Points[i], seg > 0f ? Mathf.Clamp01(d / seg) : 1f);
                d -= seg;
            }
            return Points[Points.Length - 1];
        }

        public Pose Sample(float t)
        {
            float u = Duration <= 0f ? 1f : Mathf.Clamp01(t / Duration);
            switch (Ease)
            {
                case Kind.Brake:
                    // keeps the speed it came in with and brakes it to zero
                    float s = u * Duration;
                    return new Pose(A.P + V0 * (s - s * s / (2f * Duration)), Quaternion.Slerp(A.R, B.R, u * u * (3f - 2f * u)));
                case Kind.Path:
                    float p = u * u * u * (u * (u * 6f - 15f) + 10f);
                    return new Pose(Along(p * Length), Quaternion.Slerp(A.R, B.R, p));
                case Kind.SetDown:
                    float d = 1f - (1f - u) * (1f - u) * (1f - u);
                    return new Pose(Vector3.LerpUnclamped(A.P, B.P, d), Quaternion.Slerp(A.R, B.R, d));
                default:
                    float e = u * u * u * (u * (u * 6f - 15f) + 10f);
                    return new Pose(Vector3.LerpUnclamped(A.P, B.P, e), Quaternion.Slerp(A.R, B.R, e));
            }
        }
    }

    /// <summary>A ship held by a beam: landing on a pad or leaving it.</summary>
    internal sealed class TractorOp
    {
        public uint Child, Parent;
        public ushort Pad;
        public bool Launch;
        public readonly List<Leg> Legs = new List<Leg>();
        public float T;
        public bool Arrived;
        public Pose Landed, Exit;

        public float Total
        {
            get
            {
                float t = 0f;
                foreach (Leg l in Legs) t += l.Duration;
                return t;
            }
        }

        public Pose Sample(float t)
        {
            foreach (Leg l in Legs)
            {
                if (t <= l.Duration) return l.Sample(t);
                t -= l.Duration;
            }
            Leg last = Legs[Legs.Count - 1];
            return last.Sample(last.Duration);
        }
    }

    /// <summary>
    /// The host side of the beam. A pilot asks to land (or to leave); the host checks the request, then moves the ship
    /// itself along a path in the carrier's frame: it brakes, is carried above the pad while it turns upright and lines
    /// up, and is set down on it. While the beam holds it the ship's rigidbody is kinematic and follows the path
    /// (predicted one tick ahead with the carrier's motion, so it keeps up with a moving carrier); its thrusters and
    /// gravity do nothing. Once it touches down it becomes a joint of the carrier (<see cref="Docks"/>). Leaving plays the
    /// path back: lifted off the pad, carried back to where the beam caught it, and released at the carrier's speed.
    /// </summary>
    internal static class Tractor
    {
        public const float HoverHeight = 1.5f;
        public const float MaxRelativeSpeed = 12f;
        public const float MaxRelativeSpin = 0.8f;
        public const float SetDownSeconds = 1.8f;
        public const float LiftSeconds = 1.6f;
        public const float BrakeDistance = 1.5f;
        private const float Shrink = 0.05f;

        public static readonly Dictionary<uint, TractorOp> Ops = new Dictionary<uint, TractorOp>();

        private static readonly AccessTools.FieldRef<ShipRigidbody, Rigidbody> RigidbodyRef = AccessTools.FieldRefAccess<ShipRigidbody, Rigidbody>("_rigidbody");
        private static readonly AccessTools.FieldRef<TrackedShipServer, ShipServerPhysics> PhysicsRef = AccessTools.FieldRefAccess<TrackedShipServer, ShipServerPhysics>("_shipServerPhysics");
        private static MethodInfo _isAnchored;
        private static bool _anchorLooked;
        private static int _mask;
        private static readonly Collider[] Hits = new Collider[64];

        public static bool IsBusy(uint ship) => Ops.ContainsKey(ship);

        public static bool PadBusy(uint parent, ushort pad) => Docks.OnPad(parent, pad) != null || Ops.Values.Any(o => o.Parent == parent && o.Pad == pad);

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(ShipServerPhysics), "UpdateRootRigidbody"),
                prefix: new HarmonyMethod(typeof(Tractor), nameof(DrivePrefix)));
            h.Patch(AccessTools.Method("Managers.Server.WorldPhysicsServerUpdater:PrePhysics"),
                prefix: new HarmonyMethod(typeof(Tractor), nameof(PrePhysicsPrefix)));
            h.Patch(AccessTools.Method(typeof(Ships.Physics.Forces.ShipThrust), "CalculateShipThrustForce"),
                prefix: new HarmonyMethod(typeof(Tractor), nameof(ThrustPrefix)));
            h.Patch(AccessTools.Method(typeof(TrackedShipServer), nameof(TrackedShipServer.SetFlightInputs)),
                prefix: new HarmonyMethod(typeof(Tractor), nameof(FlightInputsPrefix)));
            TractorNet.OnServer(Op.Request, ServerOnRequest);
            TractorNet.OnClientHello += conn =>
            {
                foreach (TractorOp op in Ops.Values) TractorNet.SendTo(conn, Op.Beam, w => WriteBeam(w, op, op.Launch ? (byte)2 : (byte)1));
            };
        }

        public static void ResetServer() => Ops.Clear();

        // ------------------------------------------------------------------ requests

        private static void ServerOnRequest(NetworkConnection conn, BinaryReader r)
        {
            var players = Svc.PlayersServer;
            var player = players?.GetTrackedPlayerFromConnectionId(conn.ClientId);
            Verdict v;
            uint ship = 0;
            if (player == null || player.State.Type != PlayerStateType.Piloting) v = Verdict.NotPiloting;
            else v = Request(ship = player.State.EntityId);
            TractorNet.SendTo(conn, Op.Result, w =>
            {
                w.Write((byte)v);
                w.Write(ship);
            });
        }

        /// <summary>Land the ship on the pad it is in, or take it off the pad it sits on.</summary>
        public static Verdict Request(uint shipId)
        {
            if (Ops.ContainsKey(shipId)) return Verdict.Busy;
            Verdict v = Docks.IsDockedChild(shipId) ? StartLaunch(shipId) : StartLanding(shipId);
            TractorMod.Log.Msg($"Ship {shipId}: {v}.");
            return v;
        }

        private static bool Anchored(uint ship)
        {
            if (!_anchorLooked)
            {
                _anchorLooked = true;
                _isAnchored = AccessTools.TypeByName("StellarAnchor.Anchoring") is Type t ? AccessTools.Method(t, "IsAnchored") : null;
            }
            try
            {
                return _isAnchored != null && (bool)_isAnchored.Invoke(null, new object[] { ship });
            }
            catch
            {
                return false;
            }
        }

        private static Verdict StartLanding(uint shipId)
        {
            var ships = Svc.ShipsServer;
            TrackedShipServer ship = ships?.GetShip(shipId);
            if (ship == null || !ship.IsRigidbodyLoaded) return Verdict.NoZone;
            var joints = Svc.JointsServer;
            if (joints != null && joints.TryGetShipJoint(shipId, out TrackedShipJoint j) && (j.Parent != null || j.Children != null && j.Children.Count > 0))
                return Verdict.Jointed;
            if (Anchored(shipId)) return Verdict.Anchored;
            Transform ct = ship.RigidbodyObject.transform;
            Rigidbody crb = RigidbodyRef(ct.GetComponent<ShipRigidbody>());
            if (crb == null) return Verdict.NoZone;
            List<Shape> shapes = Shapes(ship);
            if (shapes.Count == 0) return Verdict.NoZone;
            Bounds box = LocalBounds(shapes);
            Vector3 center = ct.TransformPoint(box.center);

            // the free pad whose zone holds the ship's centre (a little margin: the next checks say what is wrong);
            // a pad that already holds a ship, or is busy with one, has no zone at all
            TrackedShipServer carrier = null;
            StatefulPart pad = null;
            float best = float.MaxValue;
            foreach (TrackedShipServer other in ships.Ships)
            {
                if (other == ship || !other.IsRigidbodyLoaded || other.RigidbodyObject.scene != ct.gameObject.scene) continue;
                Transform pt = other.RigidbodyObject.transform;
                foreach (StatefulPart p in Pads.PadsOf(other))
                {
                    if (PadBusy(other.Id, p.Id)) continue;
                    var (pp, pr) = Pads.Frame(p);
                    Zone z = Pads.ZoneOf(other.Id, p);
                    Vector3 local = Quaternion.Inverse(pt.rotation * pr) * (center - pt.TransformPoint(pp));
                    if (!Pads.InZone(z, local, 2f)) continue;
                    float d = (local - z.Center).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        carrier = other;
                        pad = p;
                    }
                }
            }
            if (pad == null) return Verdict.NoZone;
            PadSpec spec = Pads.Spec(pad);
            Zone zone = Pads.ZoneOf(carrier.Id, pad);
            if (ship.HullAndPartsMass >= carrier.HullAndPartsMass) return Verdict.Heavier;

            Transform ptr = carrier.RigidbodyObject.transform;
            var (padPos, padRot) = Pads.Frame(pad);
            Quaternion padWorldRot = ptr.rotation * padRot;
            Vector3 padWorld = ptr.TransformPoint(padPos);
            foreach (Vector3 c in Corners(box))
                if (!Pads.InZone(zone, Quaternion.Inverse(padWorldRot) * (ct.TransformPoint(c) - padWorld), 0.05f))
                    return Verdict.NotInside;

            Rigidbody root = ptr.GetComponentInParent<Rigidbody>();
            Vector3 carrierVel = root != null && !root.isKinematic ? root.GetPointVelocity(center) : Vector3.zero;
            Vector3 relVel = crb.linearVelocity - carrierVel;
            if (relVel.magnitude > MaxRelativeSpeed) return Verdict.TooFast;
            Vector3 carrierSpin = root != null && !root.isKinematic ? root.angularVelocity : Vector3.zero;
            if ((crb.angularVelocity - carrierSpin).magnitude > MaxRelativeSpin) return Verdict.Spinning;

            // landed pose: upright on the pad, heading turned to the nearest quarter of the pad's forward
            Quaternion inPad = Quaternion.Inverse(padWorldRot) * ct.rotation;
            Vector3 f = inPad * Vector3.forward;
            f.y = 0f;
            float yaw = f.sqrMagnitude < 1e-4f ? 0f : Mathf.Round(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg / 90f) * 90f;
            Quaternion landedInPad = Quaternion.Euler(0f, yaw, 0f);
            Bounds turned = Turned(box, landedInPad);
            if (turned.extents.x > spec.Width * 0.5f || turned.extents.z > spec.Width * 0.5f || turned.size.y + HoverHeight > spec.Height)
                return Verdict.TooBig;
            Vector3 bottom = new Vector3(box.center.x, box.min.y, box.center.z);
            Vector3 landedPosInPad = new Vector3(0f, Pads.Gap, 0f) - landedInPad * bottom;
            var landed = new Pose(padPos + padRot * landedPosInPad, padRot * landedInPad);
            var hover = new Pose(landed.P + padRot * Vector3.up * HoverHeight, landed.R);

            // where it is now, in the carrier's frame
            Quaternion inv = Quaternion.Inverse(ptr.rotation);
            var start = new Pose(inv * (ct.position - ptr.position), inv * ct.rotation);
            Vector3 v0 = inv * relVel;
            // the beam stops it within BrakeDistance (at most ~2.5 g for a ship at the speed limit)
            float brake = Mathf.Clamp(2f * BrakeDistance / Mathf.Max(v0.magnitude, 0.01f), 0.25f, 1f);
            var caught = new Pose(start.P + v0 * (brake * 0.5f), start.R);

            // the spot itself first: no route helps if the ship does not fit on the pad
            var settle = new TractorOp { Child = shipId, Parent = carrier.Id, Pad = pad.Id };
            settle.Legs.Add(new Leg { Ease = Leg.Kind.SetDown, A = hover, B = landed, Duration = SetDownSeconds });
            if (!PathClear(ship, ct, shapes, ptr, settle, 0)) return Verdict.NoRoom;

            foreach (Vector3[] route in Routes(caught.P, hover.P, padPos, padRot))
            {
                var op = new TractorOp { Child = shipId, Parent = carrier.Id, Pad = pad.Id, Landed = landed, Exit = caught };
                op.Legs.Add(new Leg { Ease = Leg.Kind.Brake, A = start, B = caught, V0 = v0, Duration = brake });
                op.Legs.Add(Carry(caught, hover, route));
                op.Legs.Add(new Leg { Ease = Leg.Kind.SetDown, A = hover, B = landed, Duration = SetDownSeconds });
                if (!PathClear(ship, ct, shapes, ptr, op, 1)) continue;
                Begin(op);
                return Verdict.Landing;
            }
            return Verdict.Blocked;
        }

        /// <summary>
        /// Ways from a point of the zone to the hover point above the pad (carrier frame), in the order they are tried:
        /// straight; across at the zone's height then down; up over everything then across then down (higher and higher).
        /// The first one the ship fits through is used, and played backwards to leave.
        /// </summary>
        private static IEnumerable<Vector3[]> Routes(Vector3 from, Vector3 to, Vector3 padPos, Quaternion padRot)
        {
            Quaternion inv = Quaternion.Inverse(padRot);
            Vector3 a = inv * (from - padPos), b = inv * (to - padPos);
            Vector3 C(Vector3 v) => padPos + padRot * v;
            yield return new[] { from, to };
            if (Mathf.Abs(a.y - b.y) > 0.5f)
                yield return new[] { from, C(new Vector3(b.x, a.y, b.z)), to };
            foreach (float extra in new[] { 0f, 3f, 8f, 16f })
            {
                float top = Mathf.Max(a.y, b.y) + extra;
                yield return new[] { from, C(new Vector3(a.x, top, a.z)), C(new Vector3(b.x, top, b.z)), to };
            }
        }

        private static Leg Carry(Pose a, Pose b, Vector3[] route)
        {
            var leg = new Leg { Ease = Leg.Kind.Path, A = a, B = b, Points = route };
            leg.Duration = Mathf.Clamp(leg.Length / 3.5f + Quaternion.Angle(a.R, b.R) / 45f + (route.Length - 2) * 0.6f, 1.5f, 16f);
            return leg;
        }

        private static Verdict StartLaunch(uint shipId)
        {
            var ships = Svc.ShipsServer;
            DockRecord rec = Docks.Server[shipId];
            TrackedShipServer ship = ships?.GetShip(shipId), carrier = ships?.GetShip(rec.Parent);
            if (ship == null || carrier == null || !ship.IsRigidbodyLoaded || !carrier.IsRigidbodyLoaded) return Verdict.Busy;
            if (!carrier.TryGetStatefulPart(rec.Pad, out StatefulPart pad)) return Verdict.Busy;
            Transform ct = ship.RigidbodyObject.transform, ptr = carrier.RigidbodyObject.transform;
            List<Shape> shapes = Shapes(ship);
            var (padPos, padRot) = Pads.Frame(pad);
            Vector3 up = padRot * Vector3.up;
            var landed = new Pose(rec.LocalPos, rec.LocalRot);
            var hover = new Pose(landed.P + up * HoverHeight, landed.R);
            PadSpec spec = Pads.Spec(pad);
            float rise = Mathf.Max(0f, spec.Height - LocalBounds(shapes).size.y - HoverHeight - 0.5f);
            Pose[] exits = rec.HasExit
                ? new[] { new Pose(rec.ExitPos, rec.ExitRot), new Pose(hover.P + up * rise, hover.R), hover }
                : new[] { new Pose(hover.P + up * rise, hover.R), hover };
            foreach (Pose exit in exits)
            {
                bool moves = (exit.P - hover.P).sqrMagnitude > 0.01f || Quaternion.Angle(exit.R, hover.R) > 1f;
                IEnumerable<Vector3[]> routes = moves
                    ? Routes(exit.P, hover.P, padPos, padRot).Select(r => r.Reverse().ToArray())
                    : new[] { (Vector3[])null };
                foreach (Vector3[] route in routes)
                {
                    var op = new TractorOp { Child = shipId, Parent = rec.Parent, Pad = rec.Pad, Launch = true, Landed = landed, Exit = exit };
                    op.Legs.Add(new Leg { Ease = Leg.Kind.Smooth, A = landed, B = hover, Duration = LiftSeconds });
                    if (route != null) op.Legs.Add(Carry(hover, exit, route));
                    if (!PathClear(ship, ct, shapes, ptr, op, 0)) continue;
                    Docks.Remove(shipId);
                    Begin(op);
                    return Verdict.Launching;
                }
            }
            return Verdict.Blocked;
        }

        private static float CarrySeconds(Pose a, Pose b) =>
            Mathf.Clamp(Vector3.Distance(a.P, b.P) / 3.5f + Quaternion.Angle(a.R, b.R) / 45f, 1.5f, 10f);

        private static void Begin(TractorOp op)
        {
            Ops[op.Child] = op;
            TractorNet.SendToAll(Op.Beam, w => WriteBeam(w, op, op.Launch ? (byte)2 : (byte)1));
        }

        private static void WriteBeam(BinaryWriter w, TractorOp op, byte kind)
        {
            w.Write(op.Child);
            w.Write(op.Parent);
            w.Write(op.Pad);
            w.Write(kind);
            w.Write(Mathf.Max(0f, op.Total - op.T));
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>Before the joints update: landed ships become joints, broken beams let go.</summary>
        private static void PrePhysicsPrefix()
        {
            try
            {
                var ships = Svc.ShipsServer;
                if (ships == null) return;
                if (Ops.Count > 0)
                {
                    List<TractorOp> done = null;
                    foreach (TractorOp op in Ops.Values)
                    {
                        TrackedShipServer ship = ships.GetShip(op.Child), carrier = ships.GetShip(op.Parent);
                        bool broken = ship == null || carrier == null || !ship.IsRigidbodyLoaded || !carrier.IsRigidbodyLoaded
                            || ship.RigidbodyObject.scene != carrier.RigidbodyObject.scene
                            || !carrier.TryGetStatefulPart(op.Pad, out StatefulPart pad) || !Pads.IsPad(pad);
                        if (broken || op.Arrived) (done ??= new List<TractorOp>()).Add(op);
                        if (broken) TractorMod.Log.Msg($"Beam on ship {op.Child} broken.");
                    }
                    if (done != null)
                        foreach (TractorOp op in done)
                        {
                            Ops.Remove(op.Child);
                            TractorNet.SendToAll(Op.Beam, w => WriteBeam(w, op, 0));
                            if (op.Arrived && !op.Launch)
                                Docks.Add(new DockRecord
                                {
                                    Child = op.Child, Parent = op.Parent, Pad = op.Pad, LocalPos = op.Landed.P, LocalRot = op.Landed.R,
                                    HasExit = true, ExitPos = op.Exit.P, ExitRot = op.Exit.R,
                                });
                        }
                }
                Docks.ServerTick();
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Tractor tick failed: {ex}");
            }
        }

        /// <summary>Instead of the game's forces: a held ship follows its path as a kinematic body.</summary>
        private static bool DrivePrefix(float deltaTime, ShipState ____networkState, ShipRigidbody ____shipRigidbody)
        {
            if (Ops.Count == 0 || ____networkState == null || !Ops.TryGetValue(____networkState.Id, out TractorOp op)) return true;
            Rigidbody rb = ____shipRigidbody != null ? RigidbodyRef(____shipRigidbody) : null;
            TrackedShipServer carrier = Svc.ShipsServer?.GetShip(op.Parent);
            if (rb == null || carrier == null || !carrier.IsRigidbodyLoaded) return true;
            Transform pt = carrier.RigidbodyObject.transform;
            op.T += deltaTime;
            float total = op.Total;
            if (op.Launch && op.T >= total)
            {
                // let go at the carrier's speed; the game takes the ship back from here
                Ops.Remove(op.Child);
                Rigidbody root = pt.GetComponentInParent<Rigidbody>();
                Vector3 v = root != null && !root.isKinematic ? root.GetPointVelocity(rb.worldCenterOfMass) : Vector3.zero;
                ____networkState.ServerSetPhysicsState(new ShipPhysicsState(____networkState.PhysicsState.SpaceId, rb.position, rb.rotation.normalized, v));
                TractorNet.SendToAll(Op.Beam, w => WriteBeam(w, op, 0));
                return true;
            }
            Pose local = op.Sample(Mathf.Min(op.T, total));
            var (pp, pr) = Predicted(pt, deltaTime);
            rb.isKinematic = true;
            rb.MovePosition(pp + pr * local.P);
            rb.MoveRotation((pr * local.R).normalized);
            if (op.T >= total) op.Arrived = true;
            return false;
        }

        /// <summary>The carrier's pose after this tick's simulation, from its rigid body's velocity.</summary>
        private static (Vector3, Quaternion) Predicted(Transform t, float dt)
        {
            Rigidbody root = t.GetComponentInParent<Rigidbody>();
            if (root == null || root.isKinematic) return (t.position, t.rotation);
            Vector3 com = root.worldCenterOfMass, w = root.angularVelocity;
            Quaternion dq = w.sqrMagnitude > 1e-12f ? Quaternion.AngleAxis(w.magnitude * dt * Mathf.Rad2Deg, w.normalized) : Quaternion.identity;
            return (com + root.linearVelocity * dt + dq * (t.position - com), dq * t.rotation);
        }

        /// <summary>A landed ship pushes nothing: its thrust no longer adds to the carrier's.</summary>
        private static bool ThrustPrefix(IShipStateRead stateRead, ref Vector3 __result)
        {
            if (stateRead == null || !Docks.IsDockedChild(stateRead.Id)) return true;
            __result = Vector3.zero;
            return false;
        }

        /// <summary>Its pilot's controls do nothing either while it is landed or held by the beam.</summary>
        private static void FlightInputsPrefix(TrackedShipServer __instance, ref FlightControlsNetworkInputs inputs)
        {
            if (!Docks.IsDockedChild(__instance.Id) && !Ops.ContainsKey(__instance.Id)) return;
            inputs.MoveInput = Vector3.zero;
            inputs.RotationInput = Vector3.zero;
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>A box of the ship's own colliders, in its rigidbody's frame.</summary>
        internal struct Shape
        {
            public Vector3 Center, Half;
            public Quaternion Rot;
        }

        /// <summary>The ship's hull and part colliders (not the players riding in it), as boxes in its frame.</summary>
        internal static List<Shape> Shapes(TrackedShipServer ship)
        {
            var list = new List<Shape>();
            GameObject cols = PhysicsRef(ship)?.IsColliderObjectCreated == true ? PhysicsRef(ship).CollidersObject : null;
            if (cols == null) return list;
            Transform rbt = ship.RigidbodyObject.transform;
            Quaternion inv = Quaternion.Inverse(rbt.rotation);
            foreach (Collider c in cols.GetComponentsInChildren<Collider>(false))
            {
                if (!c.enabled || c.isTrigger) continue;
                Transform t = c.transform;
                Vector3 center, size;
                Quaternion rot = inv * t.rotation;
                switch (c)
                {
                    case BoxCollider b:
                        center = b.center;
                        size = b.size;
                        break;
                    case MeshCollider m when m.sharedMesh != null:
                        center = m.sharedMesh.bounds.center;
                        size = m.sharedMesh.bounds.size;
                        break;
                    default:
                        Bounds wb = c.bounds;
                        list.Add(new Shape { Center = rbt.InverseTransformPoint(wb.center), Half = wb.extents, Rot = inv });
                        continue;
                }
                Vector3 s = t.lossyScale;
                list.Add(new Shape
                {
                    Center = rbt.InverseTransformPoint(t.TransformPoint(center)),
                    Half = new Vector3(Mathf.Abs(size.x * s.x), Mathf.Abs(size.y * s.y), Mathf.Abs(size.z * s.z)) * 0.5f,
                    Rot = rot,
                });
            }
            return list;
        }

        private static IEnumerable<Vector3> Corners(Shape s)
        {
            for (int i = 0; i < 8; i++)
                yield return s.Center + s.Rot * Vector3.Scale(s.Half, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        internal static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        internal static Bounds LocalBounds(List<Shape> shapes)
        {
            var b = new Bounds();
            bool first = true;
            foreach (Shape s in shapes)
                foreach (Vector3 c in Corners(s))
                {
                    if (first) b = new Bounds(c, Vector3.zero);
                    else b.Encapsulate(c);
                    first = false;
                }
            return b;
        }

        private static Bounds Turned(Bounds b, Quaternion r)
        {
            var o = new Bounds(r * b.center, Vector3.zero);
            foreach (Vector3 c in Corners(b)) o.Encapsulate(r * c);
            return o;
        }

        private static int Mask
        {
            get
            {
                if (_mask != 0) return _mask;
                int m = Physics.AllLayers;
                for (int i = 0; i < 32; i++)
                {
                    string n = LayerMask.LayerToName(i);
                    if (n.Contains("Player") || n.Contains("UI") || n.Contains("Ignore") || n.Contains("Water")) m &= ~(1 << i);
                }
                return _mask = m;
            }
        }

        /// <summary>
        /// The ship's boxes (a little shrunk) at several points of the path, against everything but itself: the
        /// carrier's hull and parts, players standing in the way, other ships, the ground.
        /// </summary>
        private static bool PathClear(TrackedShipServer ship, Transform ct, List<Shape> shapes, Transform carrier, TractorOp op, int firstLeg)
        {
            PhysicsScene scene = ct.gameObject.scene.GetPhysicsScene();
            for (int l = firstLeg; l < op.Legs.Count; l++)
            {
                Leg leg = op.Legs[l];
                // a sample every half metre and every 10 degrees of turn
                int samples = Mathf.Clamp(Mathf.CeilToInt(leg.Length / 0.5f) + Mathf.CeilToInt(Quaternion.Angle(leg.A.R, leg.B.R) / 10f), 2, 160);
                // leaving, the ship already sits at the start of its path: what touches it there does not count
                for (int k = op.Launch && l == firstLeg ? 1 : 0; k <= samples; k++)
                {
                    Pose p = leg.Sample(leg.Duration * k / samples);
                    Vector3 pos = carrier.TransformPoint(p.P);
                    Quaternion rot = carrier.rotation * p.R;
                    foreach (Shape s in shapes)
                    {
                        Vector3 half = Vector3.Max(s.Half - Vector3.one * Shrink, Vector3.one * 0.01f);
                        int n = scene.OverlapBox(pos + rot * s.Center, half, Hits, rot * s.Rot, Mask, QueryTriggerInteraction.Ignore);
                        for (int i = 0; i < n; i++)
                        {
                            if (Hits[i].transform.IsChildOf(ct)) continue;
                            TractorMod.Log.Msg($"Beam path of ship {ship.Id} blocked (leg {l}, {100 * k / samples}%) by {Hits[i].name} ({LayerMask.LayerToName(Hits[i].gameObject.layer)}).");
                            return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
