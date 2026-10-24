using System;
using System.Collections.Generic;
using Core.Services;
using HarmonyLib;
using Players.Interface.Model;
using Ships;
using Ships.Interface.Model.Parts;
using TMPro;
using TractorBeam.Core;
using TractorBeam.Docking;
using UI.Common;
using UI.Research;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TractorBeam.Client
{
    /// <summary>
    /// The pad's menu: where its landing zone is and how big. The pad is a copy of the targeting computer, so the game
    /// handles the rest: E opens it (one player at a time), the cursor is freed, Escape or leaving closes it. The game's
    /// menu opener is asked for the targeting computer's screen; for a pad it shows this window instead.
    ///
    /// Every change goes to the host, which keeps the zone within the pad's limits and sends it back to everyone. The
    /// zone is drawn around the carrier while the menu is open (yellow; orange when the pad is taken and its zone off).
    /// </summary>
    internal static class PadMenu
    {
        private static Canvas _canvas;
        private static TextMeshProUGUI _title, _status;
        private static readonly List<(Func<Zone, float> get, TextMeshProUGUI label)> Values = new List<(Func<Zone, float>, TextMeshProUGUI)>();
        private static PartContext _pad;
        private static bool _open;

        /// <summary>The pad whose menu is open on this machine.</summary>
        public static PartContext? Editing => _open ? _pad : (PartContext?)null;

        public static void Patch(HarmonyLib.Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GenericPartOpenableMenuActivator), nameof(GenericPartOpenableMenuActivator.ShowMenu)),
                prefix: new HarmonyMethod(typeof(PadMenu), nameof(ShowMenuPrefix)));
            h.Patch(AccessTools.Method(typeof(GenericPartOpenableMenuActivator), nameof(GenericPartOpenableMenuActivator.HideMenu)),
                postfix: new HarmonyMethod(typeof(PadMenu), nameof(Hide)));
        }

        public static void Reset()
        {
            _open = false;
            if (_canvas != null) Object.Destroy(_canvas.gameObject);
            _canvas = null;
            Values.Clear();
        }

        private static bool ShowMenuPrefix(GenericPartOpenableMenuActivator __instance)
        {
            if (Svc.PlayersClient == null || !Svc.PlayersClient.TryGetLocalPlayerState(out PlayerState st)) return true;
            if (!Svc.ShipsClient.TryGetTrackedShip(st.EntityId, out TrackedShipClient ship) || !ship.TryGetStatefulPart(st.PartId, out StatefulPart part) || !Pads.IsPad(part))
                return true;
            _pad = new PartContext(st.EntityId, st.PartId);
            Show(Pads.Spec(part));
            ServiceLocator.GetService<MenuTracker>()?.TrackMenu(__instance);
            return false;
        }

        private static void Hide()
        {
            _open = false;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private static void Show(PadSpec spec)
        {
            if (_canvas == null) Build();
            _title.text = Loc.T($"Rayon tracteur ({spec.Fr})", $"Tractor beam ({spec.En})");
            _canvas.gameObject.SetActive(true);
            _open = true;
            Refresh();
        }

        public static void Update()
        {
            if (!_open) return;
            if (!Current(out _, out _))
            {
                // the pad is gone (removed, or its ship unloaded): let the game close the interaction
                ServiceLocator.GetService<MenuTracker>()?.CloseAllOpenMenus();
                Hide();
                return;
            }
            Refresh();
        }

        private static bool Current(out StatefulPart part, out PadSpec spec)
        {
            part = null;
            spec = null;
            var ships = Svc.ShipsClient;
            if (ships == null || !ships.TryGetTrackedShip(_pad.ShipId, out TrackedShipClient ship) || !ship.TryGetStatefulPart(_pad.PartId, out part) || !Pads.IsPad(part))
                return false;
            spec = Pads.Spec(part);
            return true;
        }

        private static void Refresh()
        {
            if (!Current(out StatefulPart part, out _)) return;
            Zone z = Pads.ZoneOf(_pad.ShipId, part);
            foreach (var (get, label) in Values) label.text = $"{get(z):0.#} m";
            uint landed = 0;
            foreach (var kv in Docks.Known)
                if (kv.Value.Parent == _pad.ShipId && kv.Value.Pad == _pad.PartId) landed = kv.Key;
            if (landed != 0 || TractorClient.PadBusy(_pad.ShipId, _pad.PartId))
            {
                _status.text = Loc.T("Plateforme occupée : la zone est désactivée tant qu'un vaisseau y est posé.", "Pad taken: the zone is off while a ship sits on it.");
                _status.color = UiColors.Warning;
            }
            else
            {
                _status.text = Loc.T("Plateforme libre : la zone est active.", "Pad free: the zone is active.");
                _status.color = UiColors.Good;
            }
        }

        /// <summary>Moves or resizes the zone by one step (the host clamps it to the pad's limits).</summary>
        private static void Change(Func<Zone, Zone> change)
        {
            if (!Current(out StatefulPart part, out PadSpec spec)) return;
            Zone z = new PadConfig { Zone = change(Pads.ZoneOf(_pad.ShipId, part)) }.Clamped(spec).Zone;
            PadConfigs.Request(_pad, z);
        }

        // ------------------------------------------------------------------ build

        private static void Build()
        {
            _canvas = UiKit.MakeCanvas("TractorBeam.PadMenu", 30);
            RectTransform w = UiKit.Window(_canvas.transform, "PadMenu", new Vector2(900f, 0f), new Vector2(1500f, 1440f));
            _title = UiKit.Label(UiKit.Rect(w, "Title", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -40f), new Vector2(1300f, 90f), new Vector2(0f, 1f)),
                "", 68, UiKit.Text);
            UiKit.Label(UiKit.Rect(w, "Subtitle", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -130f), new Vector2(1360f, 60f), new Vector2(0f, 1f)),
                Loc.T("Zone d'appontage", "Landing zone"), 40, UiKit.TextDim);
            UiKit.Line(w, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -210f), 1360f);

            float y = -250f;
            Section(w, ref y, Loc.T("Position (depuis la plateforme)", "Position (from the pad)"));
            Row(w, ref y, Loc.T("Gauche / droite", "Left / right"), z => z.Bottom.x, (z, d) => { z.Bottom.x += d; return z; });
            Row(w, ref y, Loc.T("Arrière / avant", "Back / front"), z => z.Bottom.z, (z, d) => { z.Bottom.z += d; return z; });
            Row(w, ref y, Loc.T("Bas / haut", "Down / up"), z => z.Bottom.y, (z, d) => { z.Bottom.y += d; return z; });
            y -= 20f;
            Section(w, ref y, Loc.T("Taille", "Size"));
            Row(w, ref y, Loc.T("Largeur", "Width"), z => z.Size.x, (z, d) => { z.Size.x += d; return z; });
            Row(w, ref y, Loc.T("Profondeur", "Depth"), z => z.Size.z, (z, d) => { z.Size.z += d; return z; });
            Row(w, ref y, Loc.T("Hauteur", "Height"), z => z.Size.y, (z, d) => { z.Size.y += d; return z; });

            _status = UiKit.Label(UiKit.Rect(w, "Status", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 290f), new Vector2(-140f, 110f), new Vector2(0.5f, 0f)),
                "", 40, UiKit.Text, false, TextAlignmentOptions.Left);
            UiKit.Label(UiKit.Rect(w, "Hint", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 200f), new Vector2(-140f, 90f), new Vector2(0.5f, 0f)),
                Loc.T("La zone est tracée en jaune autour du vaisseau. Un pilote entièrement dedans appuie sur L : le rayon l'amène jusqu'à la plateforme.",
                      "The zone is drawn in yellow around the ship. A pilot entirely inside it presses L: the beam brings the ship to the pad."),
                34, UiKit.TextDim, false, TextAlignmentOptions.Left);

            Button(w, new Vector2(-370f, 70f), 620f, Loc.T("Zone sur la plateforme", "Zone on the pad"), () =>
            {
                if (Current(out _, out PadSpec spec)) PadConfigs.Request(_pad, PadConfig.Default(spec).Zone);
            });
            Button(w, new Vector2(370f, 70f), 620f, Loc.T("Fermer", "Close"), () => ServiceLocator.GetService<MenuTracker>()?.CloseAllOpenMenus());
        }

        private static void Section(RectTransform w, ref float y, string text)
        {
            UiKit.Label(UiKit.Rect(w, "Section", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, y), new Vector2(1300f, 64f), new Vector2(0f, 1f)),
                text, 46, UiKit.Accent);
            y -= 80f;
        }

        private static void Row(RectTransform w, ref float y, string name, Func<Zone, float> get, Func<Zone, float, Zone> set)
        {
            RectTransform row = UiKit.Rect(w, "Row", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, y), new Vector2(-140f, 96f), new Vector2(0.5f, 1f));
            UiKit.Label(UiKit.Rect(row, "Name", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(470f, 80f), new Vector2(0f, 0.5f)), name, 42, UiKit.Text);
            TextMeshProUGUI value = UiKit.Label(UiKit.Rect(row, "Value", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(480f, 0f), new Vector2(230f, 80f), new Vector2(0f, 0.5f)),
                "", 44, UiKit.Text, true, TextAlignmentOptions.Right);
            Values.Add((get, value));
            float[] steps = { -5f, -1f, 1f, 5f };
            for (int i = 0; i < steps.Length; i++)
            {
                float d = steps[i];
                RectTransform b = UiKit.Rect(row, "Step", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(760f + i * 150f, 0f), new Vector2(136f, 86f), new Vector2(0f, 0.5f));
                UiKit.CellButton(b, () => Change(z => set(z, d)), out _, out _);
                UiKit.Label(UiKit.Fill(b, "Text"), d > 0 ? $"+{d:0}" : $"{d:0}", 40, UiKit.Text, true, TextAlignmentOptions.Center);
            }
            y -= 106f;
        }

        private static void Button(RectTransform w, Vector2 pos, float width, string text, Action click)
        {
            RectTransform b = UiKit.Rect(w, "Button", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), pos, new Vector2(width, 96f), new Vector2(0.5f, 0f));
            UiKit.TabButton(b, text, click, out _, out _);
        }
    }
}
