using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FishNet.Connection;
using HarmonyLib;
using Managers.Interface;
using Players.Interface.Model;
using Saving.Services;
using Ships;
using Ships.Interface.Model.Parts;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Net;
using UnityEngine;

namespace ParkingTractorBeam.Docking
{
    /// <summary>
    /// A pad's settings, in the pad's frame: where its landing zone is (floor centre and size), and where a landed ship
    /// is set down (the centre of its bottom) and which way it faces.
    /// </summary>
    internal sealed class PadConfig
    {
        public Zone Zone;
        public Vector3 Park;
        /// <summary>The landed ship's heading in quarter turns from the pad's forward, or <see cref="AutoHeading"/>.</summary>
        public int Heading = AutoHeading;

        /// <summary>The quarter turn nearest to the heading the ship came in with.</summary>
        public const int AutoHeading = -1;

        public static PadConfig Default(PadSpec spec) => new PadConfig
        {
            Zone = DefaultZone(spec),
            Park = Vector3.zero,
        };

        public static Zone DefaultZone(PadSpec spec) => new Zone { Bottom = Vector3.zero, Size = new Vector3(spec.Width, spec.Height, spec.Width) };

        public PadConfig With(Zone zone) => new PadConfig { Zone = zone, Park = Park, Heading = Heading };

        public PadConfig With(Vector3 park, int heading) => new PadConfig { Zone = Zone, Park = park, Heading = heading };

        /// <summary>
        /// Keeps the settings within what the pad allows: a zone no bigger than its size, the zone and the parking spot
        /// no farther than its reach.
        /// </summary>
        public PadConfig Clamped(PadSpec spec)
        {
            Vector3 size = new Vector3(Mathf.Clamp(Zone.Size.x, MinSize, spec.Width), Mathf.Clamp(Zone.Size.y, MinSize, spec.Height), Mathf.Clamp(Zone.Size.z, MinSize, spec.Width));
            return new PadConfig
            {
                Zone = new Zone { Bottom = Round(InReach(Zone.Bottom, spec)), Size = Round(size) },
                Park = Round(InReach(Park, spec)),
                Heading = Heading < 0 ? AutoHeading : Heading % 4,
            };
        }

        public const float MinSize = 4f;

        private static Vector3 InReach(Vector3 v, PadSpec spec)
        {
            float r = spec.Reach;
            return new Vector3(Mathf.Clamp(v.x, -r, r), Mathf.Clamp(v.y, -r, r), Mathf.Clamp(v.z, -r, r));
        }

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);

        public void Write(BinaryWriter w)
        {
            w.Write(Zone.Bottom);
            w.Write(Zone.Size);
            w.Write(Park);
            w.Write((sbyte)Heading);
        }

        public static PadConfig Read(BinaryReader r) => new PadConfig
        {
            Zone = new Zone { Bottom = r.ReadVector3(), Size = r.ReadVector3() },
            Park = r.ReadVector3(),
            Heading = r.ReadSByte(),
        };
    }

    /// <summary>
    /// The settings of each pad, set from the pad's menu. The host keeps them (saved in tractorbeam-pads.txt next to
    /// world.json) and sends every change to every player, who need them to draw the zones and the parking spots and to
    /// show the landing prompt. A pad with no entry has the default settings.
    /// </summary>
    internal static class PadConfigs
    {
        private const string FileName = "tractorbeam-pads.txt";

        private static readonly Dictionary<PartContext, PadConfig> Configs = new Dictionary<PartContext, PadConfig>();

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameSaver), "SaveGame", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(PadConfigs), nameof(AfterSave)));
            h.Patch(AccessTools.Method(typeof(GameLoader), nameof(GameLoader.LoadState)),
                postfix: new HarmonyMethod(typeof(PadConfigs), nameof(AfterLoad)));
            TractorNet.OnServer(Op.SetZone, ServerOnSetZone);
            TractorNet.OnClient(Op.Zones, ClientOnZones);
            TractorNet.OnClientHello += conn => TractorNet.SendTo(conn, Op.Zones, w => WriteAll(w, Configs));
        }

        /// <summary>Forgotten with the world (the host reloads them with the next one, clients get them on joining).</summary>
        public static void Reset() => Configs.Clear();

        public static PadConfig Get(uint ship, StatefulPart pad) =>
            Configs.TryGetValue(new PartContext(ship, pad.Id), out PadConfig c) ? c : PadConfig.Default(Pads.Spec(pad));

        // ------------------------------------------------------------------ changes

        /// <summary>
        /// Client: asks the host to change a pad's settings. They are shown at once (the next click builds on them); the
        /// host's answer, clamped the same way, follows.
        /// </summary>
        public static void Request(PartContext pad, PadConfig config)
        {
            Configs[pad] = config;
            TractorNet.SendToServer(Op.SetZone, w =>
            {
                w.Write(pad.ShipId);
                w.Write(pad.PartId);
                config.Write(w);
            });
        }

        private static void ServerOnSetZone(NetworkConnection conn, BinaryReader r)
        {
            var pad = new PartContext(r.ReadUInt32(), r.ReadUInt16());
            PadConfig config = PadConfig.Read(r);
            // only the player at that pad's menu may change it
            var player = Svc.PlayersServer?.GetTrackedPlayerFromConnectionId(conn.ClientId);
            if (player == null || player.State.EntityId != pad.ShipId || player.State.PartId != pad.PartId) return;
            TrackedShipServer ship = Svc.ShipsServer?.GetShip(pad.ShipId);
            if (ship == null || !ship.TryGetStatefulPart(pad.PartId, out StatefulPart part) || !Pads.IsPad(part)) return;
            Set(pad, config.Clamped(Pads.Spec(part)));
        }

        public static void Set(PartContext pad, PadConfig config)
        {
            Configs[pad] = config;
            TractorNet.SendToAll(Op.Zones, w => WriteAll(w, new Dictionary<PartContext, PadConfig> { [pad] = config }));
        }

        private static void WriteAll(BinaryWriter w, Dictionary<PartContext, PadConfig> configs)
        {
            w.Write(configs.Count);
            foreach (var kv in configs)
            {
                w.Write(kv.Key.ShipId);
                w.Write(kv.Key.PartId);
                kv.Value.Write(w);
            }
        }

        private static void ClientOnZones(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var pad = new PartContext(r.ReadUInt32(), r.ReadUInt16());
                Configs[pad] = PadConfig.Read(r);
            }
        }

        // ------------------------------------------------------------------ saving

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static void AfterSave(string worldDirectoryFullName)
        {
            try
            {
                string path = Path.Combine(worldDirectoryFullName, FileName);
                var ships = Svc.ShipsServer;
                // pads removed since are dropped here
                var live = Configs.Where(kv => ships != null && ships.GetShip(kv.Key.ShipId) is TrackedShipServer s
                                               && s.TryGetStatefulPart(kv.Key.PartId, out StatefulPart p) && Pads.IsPad(p)).ToList();
                if (live.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                var lines = new List<string> { "# Parking Tractor Beam pads v2: ship pad  zone bottom x y z  zone size x y z  park x y z  heading (pad frame; -1 = auto)" };
                foreach (var kv in live)
                {
                    PadConfig c = kv.Value;
                    Zone z = c.Zone;
                    lines.Add(string.Join(" ", kv.Key.ShipId, kv.Key.PartId, F(z.Bottom.x), F(z.Bottom.y), F(z.Bottom.z), F(z.Size.x), F(z.Size.y), F(z.Size.z),
                        F(c.Park.x), F(c.Park.y), F(c.Park.z), c.Heading));
                }
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Saving pad settings failed: {ex.Message}");
            }
        }

        private static void AfterLoad()
        {
            Configs.Clear();
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
                    if (p.Length < 8) continue;
                    // v1 lines (Tractor Beam 0.2) have no parking spot: the ship is set down on the pad
                    Configs[new PartContext(uint.Parse(p[0]), ushort.Parse(p[1]))] = new PadConfig
                    {
                        Zone = new Zone { Bottom = new Vector3(P(p[2]), P(p[3]), P(p[4])), Size = new Vector3(P(p[5]), P(p[6]), P(p[7])) },
                        Park = p.Length >= 12 ? new Vector3(P(p[8]), P(p[9]), P(p[10])) : Vector3.zero,
                        Heading = p.Length >= 12 ? int.Parse(p[11]) : PadConfig.AutoHeading,
                    };
                }
                if (Configs.Count > 0) TractorMod.Log.Msg($"Loaded the settings of {Configs.Count} pads.");
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Loading pad settings failed: {ex.Message}");
            }
        }
    }
}
