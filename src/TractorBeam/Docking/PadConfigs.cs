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
using TractorBeam.Core;
using TractorBeam.Net;
using UnityEngine;

namespace TractorBeam.Docking
{
    /// <summary>Where a pad's landing zone is: its floor centre and size in the pad's frame.</summary>
    internal sealed class PadConfig
    {
        public Zone Zone;

        public static PadConfig Default(PadSpec spec) => new PadConfig
        {
            Zone = new Zone { Bottom = Vector3.zero, Size = new Vector3(spec.Width, spec.Height, spec.Width) },
        };

        /// <summary>Keeps the zone within what the pad allows: no bigger than its size, no farther than its reach.</summary>
        public PadConfig Clamped(PadSpec spec)
        {
            Vector3 size = new Vector3(Mathf.Clamp(Zone.Size.x, MinSize, spec.Width), Mathf.Clamp(Zone.Size.y, MinSize, spec.Height), Mathf.Clamp(Zone.Size.z, MinSize, spec.Width));
            float r = spec.Reach;
            Vector3 b = new Vector3(Mathf.Clamp(Zone.Bottom.x, -r, r), Mathf.Clamp(Zone.Bottom.y, -r, r), Mathf.Clamp(Zone.Bottom.z, -r, r));
            return new PadConfig { Zone = new Zone { Bottom = Round(b), Size = Round(size) } };
        }

        public const float MinSize = 4f;

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);
    }

    /// <summary>
    /// The zone of each pad, set from the pad's menu. The host keeps them (saved in tractorbeam-pads.txt next to
    /// world.json) and sends every change to every player, who need them to draw the zones and to show the landing prompt.
    /// A pad with no entry has the default zone.
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
        /// Client: asks the host to move or resize a pad's zone. It is shown at once (the next click builds on it); the
        /// host's answer, clamped the same way, follows.
        /// </summary>
        public static void Request(PartContext pad, Zone zone)
        {
            Configs[pad] = new PadConfig { Zone = zone };
            Send(pad, zone);
        }

        private static void Send(PartContext pad, Zone zone) => TractorNet.SendToServer(Op.SetZone, w =>
        {
            w.Write(pad.ShipId);
            w.Write(pad.PartId);
            w.Write(zone.Bottom);
            w.Write(zone.Size);
        });

        private static void ServerOnSetZone(NetworkConnection conn, BinaryReader r)
        {
            var pad = new PartContext(r.ReadUInt32(), r.ReadUInt16());
            var zone = new Zone { Bottom = r.ReadVector3(), Size = r.ReadVector3() };
            // only the player at that pad's menu may change it
            var player = Svc.PlayersServer?.GetTrackedPlayerFromConnectionId(conn.ClientId);
            if (player == null || player.State.EntityId != pad.ShipId || player.State.PartId != pad.PartId) return;
            TrackedShipServer ship = Svc.ShipsServer?.GetShip(pad.ShipId);
            if (ship == null || !ship.TryGetStatefulPart(pad.PartId, out StatefulPart part) || !Pads.IsPad(part)) return;
            Set(pad, new PadConfig { Zone = zone }.Clamped(Pads.Spec(part)));
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
                w.Write(kv.Value.Zone.Bottom);
                w.Write(kv.Value.Zone.Size);
            }
        }

        private static void ClientOnZones(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var pad = new PartContext(r.ReadUInt32(), r.ReadUInt16());
                Configs[pad] = new PadConfig { Zone = new Zone { Bottom = r.ReadVector3(), Size = r.ReadVector3() } };
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
                var lines = new List<string> { "# Tractor Beam pad zones v1: ship pad  bottom x y z  size x y z (pad frame)" };
                foreach (var kv in live)
                {
                    Zone z = kv.Value.Zone;
                    lines.Add(string.Join(" ", kv.Key.ShipId, kv.Key.PartId, F(z.Bottom.x), F(z.Bottom.y), F(z.Bottom.z), F(z.Size.x), F(z.Size.y), F(z.Size.z)));
                }
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Saving pad zones failed: {ex.Message}");
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
                    Configs[new PartContext(uint.Parse(p[0]), ushort.Parse(p[1]))] = new PadConfig
                    {
                        Zone = new Zone { Bottom = new Vector3(P(p[2]), P(p[3]), P(p[4])), Size = new Vector3(P(p[5]), P(p[6]), P(p[7])) },
                    };
                }
                if (Configs.Count > 0) TractorMod.Log.Msg($"Loaded {Configs.Count} pad zones.");
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Loading pad zones failed: {ex.Message}");
            }
        }
    }
}
