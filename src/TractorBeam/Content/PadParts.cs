using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Items.Model;
using Research.Interface.Values;
using Ships.Interface.Model.Parts;
using Ships.Interface.Settings;
using Ships.Parts.DockingDoor;
using Ships.Interface.Services;
using Ships.Services;
using TractorBeam.Docking;
using TractorBeam.Visual;
using UI.BuildMenu;
using UnityEngine;
using UnityEngine.Localization;
using Object = UnityEngine.Object;

namespace TractorBeam.Content
{
    /// <summary>
    /// The three tractor pads. Each is a copy of the game's signal display (so placement, saving, network sync and the
    /// build tool all come from the game) with our model; the display screen is hidden. They sit next to the docking
    /// door: same build menu row, unlocked by the same research.
    /// </summary>
    internal static class PadParts
    {
        private const ushort DisplayId = 82;

        private static readonly Dictionary<ushort, PartSettings> Parts = new Dictionary<ushort, PartSettings>();
        private static bool _building;

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PartSettingsProvider), "EnsureDictIsCached"),
                postfix: new HarmonyMethod(typeof(PadParts), nameof(EnsureDictPostfix)));
            h.Patch(AccessTools.Method(typeof(BuildMenu), "Start"),
                prefix: new HarmonyMethod(typeof(PadParts), nameof(BuildMenuStartPrefix)));
        }

        /// <summary>Parts are looked up through cached dictionaries; make sure ours are in every one.</summary>
        private static void EnsureDictPostfix(PartSettingsProvider __instance)
        {
            if (_building) return;
            var dict = Traverse.Create(__instance).Field("_partsDict").GetValue<Dictionary<ushort, PartSettings>>();
            if (dict == null) return;
            EnsureParts();
            foreach (var kv in Parts)
                if (kv.Value != null && !dict.ContainsKey(kv.Key))
                    dict[kv.Key] = kv.Value;
        }

        public static void EnsureParts()
        {
            if (Parts.Count > 0 && Parts.Values.All(p => p != null)) return;
            _building = true;
            try
            {
                PartSettings[] all = Resources.FindObjectsOfTypeAll<PartSettings>();
                PartSettings display = all.FirstOrDefault(p => p.id == DisplayId);
                if (display == null) return;
                // the game indexes parts by id: a second part with one of our ids would break every world load
                PartSettings taken = all.FirstOrDefault(p => Pads.Spec(p.id) != null && !p.name.StartsWith("TractorBeam."));
                if (taken != null)
                {
                    TractorMod.Log.Error($"Part id {taken.id} is already used by \"{taken.name}\" (another mod): tractor pads disabled.");
                    return;
                }
#if DEV
                TractorMod.Log.Msg("[DEV] part ids >= 9000: " + string.Join(", ", all.Where(p => p.id >= 9000).Select(p => p.id).Distinct().OrderBy(i => i)));
#endif
                ItemSettings[] items = ItemList();
                Parts.Clear();
                for (int i = 0; i < Pads.Specs.Length; i++)
                    Parts[Pads.Specs[i].Id] = Build(display, Pads.Specs[i], i + 1, items);
                TractorMod.Log.Msg($"Registered {Parts.Count} tractor pads.");
            }
            catch (Exception ex)
            {
                TractorMod.Log.Error($"Tractor pads failed: {ex}");
            }
            finally
            {
                _building = false;
            }
        }

        private static ItemSettings[] ItemList()
        {
            ItemSettingsList list = Resources.FindObjectsOfTypeAll<ItemSettingsList>().FirstOrDefault();
            return list != null ? Traverse.Create(list).Field("items").GetValue<ItemSettings[]>() : new ItemSettings[0];
        }

        private static PartSettings Build(PartSettings display, PadSpec spec, int size, ItemSettings[] items)
        {
            PartSettings p = Object.Instantiate(display);
            p.name = "TractorBeam.Pad" + spec.Id;
            p.id = spec.Id;
            string w = spec.Width.ToString("0"), hgt = spec.Height.ToString("0");
            p.fullLabel = Loc.T($"Rayon tracteur ({spec.Fr})", $"Tractor beam ({spec.En})");
            p.description = Loc.T(
                $"Plateforme d'appontage : zone de {w} × {w} m sur {hgt} m de haut au-dessus d'elle. Un pilote dont le vaisseau est entièrement dans la zone appuie sur la touche d'appontage (L) : le rayon le saisit et le pose sur la plateforme, où il fait corps avec le vaisseau porteur. La même touche le fait repartir.",
                $"Landing pad: a {w} × {w} m zone, {hgt} m high, above it. A pilot whose ship is entirely inside the zone presses the landing key (L): the beam catches the ship and sets it down on the pad, where it becomes part of the carrier. The same key lets it go again.");
            p.localizedDescription = new LocalizedString();
            p.mass = 40f * size;
            var cost = new List<ItemInstance>();
            foreach (var (item, qty) in new[] { (101u, 10 * size), (112u, 6 * size), (103u, size) })
            {
                ItemSettings s = items.FirstOrDefault(i => i.id == item);
                if (s != null) cost.Add(new ItemInstance(s, qty));
            }
            Traverse.Create(p).Field("buildingCost").SetValue(cost);
            Object.DontDestroyOnLoad(p);

            GameObject prefab = Object.Instantiate(display.part, Assets.TemplateRoot);
            prefab.name = "TractorBeam.Pad" + spec.Id;
            var boundsCenter = new Vector3(0f, Pads.TopHeight * 0.5f, 0f);
            var boundsSize = new Vector3(1f, Pads.TopHeight, 1f);
            ShipPartBounds bounds = prefab.GetComponent<ShipPartBounds>();
            if (bounds != null)
            {
                bounds.center = boundsCenter;
                bounds.bounds = boundsSize;
            }
            Transform visuals = prefab.transform.Find("Visuals") ?? prefab.transform;
            foreach (Transform t in visuals.Cast<Transform>().ToList())
                t.gameObject.SetActive(false);
            PadModel.Build(size).transform.SetParent(visuals, false);
            BoxCollider box = prefab.transform.Find("Interactions")?.GetComponentInChildren<BoxCollider>(true);
            if (box != null)
            {
                box.transform.localPosition = Vector3.zero;
                box.transform.localRotation = Quaternion.identity;
                box.center = boundsCenter;
                box.size = boundsSize;
            }
            // the display's signal input is not used: tuck it under the plate
            foreach (var socket in prefab.GetComponentsInChildren<Ships.Interface.Model.Parts.Common.SocketObject>(true))
                socket.transform.localPosition = new Vector3(0f, 0.03f, -0.45f);
            p.part = prefab;
            return p;
        }

        private static bool IsDockingDoor(PartSettings p) =>
            p != null && p.part != null && p.part.GetComponentInChildren<DockingDoorInteractions>(true) != null;

        // ------------------------------------------------------------------ build menu + research

        private static void BuildMenuStartPrefix(BuildMenu __instance)
        {
            EnsureParts();
            if (Parts.Count == 0) return;
            var tabs = Traverse.Create(__instance).Field("tabsConfig").GetValue<BuildMenuTabConfig[]>();
            if (tabs == null || tabs.Any(t => t.rows != null && t.rows.Any(r => r.parts != null && r.parts.Contains(Parts[Pads.SmallId])))) return;
            var row = new BuildMenuTabConfig.BuildMenuRow { parts = Pads.Specs.Select(s => Parts[s.Id]).ToArray() };
            BuildMenuTabConfig home = tabs.FirstOrDefault(t => t.rows != null && t.rows.Any(r => r.parts != null && r.parts.Any(IsDockingDoor)));
            if (home != null)
            {
                home.rows = home.rows.Concat(new[] { row }).ToArray();
                return;
            }
            var tab = new BuildMenuTabConfig { name = "Hangar", icon = tabs.Last().icon, rows = new[] { row } };
            Traverse.Create(__instance).Field("tabsConfig").SetValue(tabs.Concat(new[] { tab }).ToArray());
        }

        public static void RegisterResearch()
        {
            EnsureParts();
            if (Parts.Count == 0) return;
            PartSettings[] pads = Pads.Specs.Select(s => Parts[s.Id]).ToArray();
            foreach (ResearchSettingsList list in Resources.FindObjectsOfTypeAll<ResearchSettingsList>())
            {
                if (list.list.Any(r => r != null && r.unlockedParts != null && r.unlockedParts.Contains(pads[0]))) continue;
                ResearchSettings home = list.list.FirstOrDefault(r => r != null && r.unlockedParts != null && r.unlockedParts.Any(IsDockingDoor))
                                        ?? list.list.FirstOrDefault(r => r != null && r.unlockedParts != null && r.unlockedParts.Length > 0);
                if (home == null) continue;
                home.unlockedParts = home.unlockedParts.Concat(pads).ToArray();
                TractorMod.Log.Msg($"Tractor pads unlocked with research \"{home.techName}\".");
            }
        }
    }
}
