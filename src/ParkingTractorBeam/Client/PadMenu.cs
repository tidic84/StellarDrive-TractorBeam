using System;
using System.Collections.Generic;
using Core.Services;
using HarmonyLib;
using Players.Interface.Model;
using Ships;
using Ships.Interface.Model.Parts;
using TMPro;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Docking;
using UI.Common;
using UI.Research;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ParkingTractorBeam.Client
{
    /// <summary>
    /// The pad's menu: where its landing zone is and how big, where a landed ship is parked and which way it faces,
    /// and when zones are drawn. The pad is a copy of the targeting computer, so the game handles the rest: E opens it
    /// (one player at a time), the cursor is freed, Escape or leaving closes it. The game's menu opener is asked for the
    /// targeting computer's screen; for a pad it shows this window instead.
    ///
    /// Every change goes to the host, which keeps the settings within the pad's limits and sends them back to everyone.
    /// While the menu is open the zone (yellow; orange when the pad is taken and its zone off) and the parking spot
    /// (violet) are drawn around the carrier through the hull, and a camera beside the controls shows them from outside.
    /// </summary>
    internal static class PadMenu
    {
        private enum Tab { Zone, Park }

        private static Canvas _canvas;
        private static TextMeshProUGUI _title, _status, _hint;
        private static readonly List<(Func<PadConfig, string> get, TextMeshProUGUI label)> Values = new List<(Func<PadConfig, string>, TextMeshProUGUI)>();
        private static RectTransform _zonePage, _parkPage;
        private static Image _zoneTab, _parkTab;
        private static TextMeshProUGUI _display;
        private static PartContext _pad;
        private static bool _open;
        private static Tab _tab = Tab.Zone;

        private static Camera _camera;
        private static RenderTexture _texture;
        private static RawImage _view;
        private static float _yaw = 215f, _pitch = 28f, _zoom = 1f;
        private static Vector3 _pivot;
        private static float _distance;
        private static bool _snap;

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
            if (_camera != null) Object.Destroy(_camera.gameObject);
            _camera = null;
            if (_texture != null) _texture.Release();
            _texture = null;
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
            if (_camera != null) _camera.enabled = false;
        }

        private static void Show(PadSpec spec)
        {
            if (_canvas == null) Build();
            _title.text = $"Parking tractor beam ({spec.Name})";
            _canvas.gameObject.SetActive(true);
            _open = true;
            _snap = true;
            SetTab(_tab);
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
            UpdatePreview();
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
            PadConfig c = PadConfigs.Get(_pad.ShipId, part);
            foreach (var (get, label) in Values) label.text = get(c);
            _display.text = DisplayName(TractorClient.Display);
            uint landed = 0;
            foreach (var kv in Docks.Known)
                if (kv.Value.Parent == _pad.ShipId && kv.Value.Pad == _pad.PartId) landed = kv.Key;
            if (landed != 0 || TractorClient.PadBusy(_pad.ShipId, _pad.PartId))
            {
                _status.text = "Pad taken: the zone is off while a ship is parked on it.";
                _status.color = UiColors.Warning;
            }
            else
            {
                _status.text = "Pad free: the zone is active.";
                _status.color = UiColors.Good;
            }
        }

        /// <summary>Changes the pad's settings by one step (the host clamps them to the pad's limits).</summary>
        private static void Change(Func<PadConfig, PadConfig> change)
        {
            if (!Current(out StatefulPart part, out PadSpec spec)) return;
            PadConfigs.Request(_pad, change(PadConfigs.Get(_pad.ShipId, part)).Clamped(spec));
        }

        private static void SetTab(Tab tab)
        {
            _tab = tab;
            _zonePage.gameObject.SetActive(tab == Tab.Zone);
            _parkPage.gameObject.SetActive(tab == Tab.Park);
            _zoneTab.color = tab == Tab.Zone ? UiKit.Accent : new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.25f);
            _parkTab.color = tab == Tab.Park ? UiKit.Accent : new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.25f);
            _hint.text = tab == Tab.Zone
                ? "The zone is drawn in yellow. A pilot whose ship is in it (centre inside, sticking out only a little) presses L: the beam brings the ship to the parking spot."
                : "The parking spot is drawn in violet: the biggest ship the pad takes, set down there, the arrow towards its nose.";
            _snap = true;
        }

        private static string HeadingName(int heading)
        {
            switch (heading)
            {
                case 0: return "Front";
                case 1: return "Right";
                case 2: return "Back";
                case 3: return "Left";
                default: return "Auto";
            }
        }

        private static string DisplayName(ZoneDisplay d)
        {
            switch (d)
            {
                case ZoneDisplay.Piloting: return "When piloting";
                case ZoneDisplay.Never: return "Never";
                default: return "Always";
            }
        }

        // ------------------------------------------------------------------ preview

        /// <summary>
        /// The camera beside the controls: it hangs under the carrier's visual object and looks at the zone (or the
        /// parking spot) from outside; drag to turn around it, scroll to zoom.
        /// </summary>
        private static void UpdatePreview()
        {
            if (!Current(out StatefulPart part, out PadSpec spec) || !Svc.ShipsClient.TryGetTrackedShip(_pad.ShipId, out TrackedShipClient ship)
                || !ship.IsVisualLoaded || ship.VisualObject == null)
                return;
            Transform vt = ship.VisualObject.transform;
            if (_camera == null)
            {
                var go = new GameObject("ParkingTractorBeam.Preview");
                _camera = go.AddComponent<Camera>();
                if (Camera.main != null) _camera.CopyFrom(Camera.main);
                _camera.fieldOfView = 50f;
                _camera.nearClipPlane = 0.3f;
            }
            // as big as the screen: the game's render passes are made for it (another size fails every frame);
            // the view shows the middle of it
            if (_texture == null || _texture.width != Screen.width || _texture.height != Screen.height)
            {
                if (_texture != null) _texture.Release();
                _texture = new RenderTexture(Screen.width, Screen.height, 24) { name = "ParkingTractorBeam.Preview" };
                Rect r = _view.rectTransform.rect;
                float u = Mathf.Clamp01(r.width / r.height / ((float)Screen.width / Screen.height));
                _view.uvRect = u < 1f ? new Rect((1f - u) * 0.5f, 0f, u, 1f) : new Rect(0f, 0f, 1f, 1f);
                _view.texture = _texture;
            }
            if (_camera.targetTexture != _texture) _camera.targetTexture = _texture;
            if (_camera.transform.parent != vt) _camera.transform.SetParent(vt, false);
            _camera.enabled = true;

            // what to frame, in the pad's frame: the pad and the zone, or the pad and the parking spot
            PadConfig c = PadConfigs.Get(_pad.ShipId, part);
            var b = new Bounds(Vector3.zero, Vector3.one * 4f);
            if (_tab == Tab.Zone)
            {
                b.Encapsulate(c.Zone.Bottom - new Vector3(c.Zone.Size.x, 0f, c.Zone.Size.z) * 0.5f);
                b.Encapsulate(c.Zone.Bottom + new Vector3(c.Zone.Size.x * 0.5f, c.Zone.Size.y, c.Zone.Size.z * 0.5f));
            }
            else
            {
                b.Encapsulate(c.Park - new Vector3(spec.Width, 0f, spec.Width) * 0.5f);
                b.Encapsulate(c.Park + new Vector3(spec.Width * 0.5f, spec.Height, spec.Width * 0.5f));
            }
            float distance = b.extents.magnitude / Mathf.Sin(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * _zoom;
            float k = _snap ? 1f : 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
            _snap = false;
            _pivot = Vector3.Lerp(_pivot, b.center, k);
            _distance = Mathf.Lerp(_distance, distance, k);

            var (pp, pr) = Pads.Frame(part);
            Quaternion look = pr * Quaternion.Euler(_pitch, _yaw, 0f);
            _camera.transform.localRotation = look;
            _camera.transform.localPosition = pp + pr * _pivot - look * Vector3.forward * _distance;
            _camera.farClipPlane = Mathf.Max(_camera.farClipPlane, _distance * 4f);
        }

        /// <summary>Mouse on the preview: drag turns the camera around the target, the wheel zooms.</summary>
        internal sealed class PreviewInput : MonoBehaviour, IDragHandler, IScrollHandler
        {
            public void OnDrag(PointerEventData e)
            {
                _yaw += e.delta.x * 0.25f;
                _pitch = Mathf.Clamp(_pitch - e.delta.y * 0.25f, -80f, 85f);
            }

            public void OnScroll(PointerEventData e) => _zoom = Mathf.Clamp(_zoom * (e.scrollDelta.y > 0f ? 0.88f : e.scrollDelta.y < 0f ? 1.14f : 1f), 0.25f, 3f);
        }

        // ------------------------------------------------------------------ build

        private static void Build()
        {
            _canvas = UiKit.MakeCanvas("ParkingTractorBeam.PadMenu", 30);
            RectTransform w = UiKit.Window(_canvas.transform, "PadMenu", Vector2.zero, new Vector2(3260f, 1660f));
            _title = UiKit.Label(UiKit.Rect(w, "Title", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -40f), new Vector2(3000f, 90f), new Vector2(0f, 1f)),
                "", 68, UiKit.Text);
            UiKit.Line(w, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f), 3120f);

            // left: the preview
            RectTransform frame = UiKit.Rect(w, "Preview", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -200f), new Vector2(1500f, 1100f), new Vector2(0f, 1f));
            UiKit.Image(frame, null, Color.black, 1f, true);
            _view = UiKit.Fill(frame, "View").gameObject.AddComponent<RawImage>();
            _view.raycastTarget = true;
            _view.gameObject.AddComponent<PreviewInput>();
            UiKit.Image(UiKit.Rect(frame, "Border", Vector2.zero, Vector2.one, Vector2.zero, new Vector2(12, 12)), "menu_parts_border", Color.white, 2f);
            UiKit.Label(UiKit.Rect(w, "PreviewHint", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -1320f), new Vector2(1500f, 60f), new Vector2(0f, 1f)),
                "Drag to turn around, scroll to zoom", 34, UiKit.TextDim, false, TextAlignmentOptions.Center);

            // right: tabs and their settings
            const float x0 = 1670f;
            _zoneTab = Tab_(w, new Vector2(x0, -200f), "Landing zone", () => SetTab(Tab.Zone));
            _parkTab = Tab_(w, new Vector2(x0 + 760f, -200f), "Parking spot", () => SetTab(Tab.Park));

            _zonePage = UiKit.Rect(w, "ZonePage", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, -330f), new Vector2(1500f, 900f), new Vector2(0f, 1f));
            float y = 0f;
            Section(_zonePage, ref y, "Position (from the pad)");
            Row(_zonePage, ref y, "Left / right", c => M(c.Zone.Bottom.x), (c, d) => { Zone z = c.Zone; z.Bottom.x += d; return c.With(z); });
            Row(_zonePage, ref y, "Back / front", c => M(c.Zone.Bottom.z), (c, d) => { Zone z = c.Zone; z.Bottom.z += d; return c.With(z); });
            Row(_zonePage, ref y, "Down / up", c => M(c.Zone.Bottom.y), (c, d) => { Zone z = c.Zone; z.Bottom.y += d; return c.With(z); });
            y -= 20f;
            Section(_zonePage, ref y, "Size");
            Row(_zonePage, ref y, "Width", c => M(c.Zone.Size.x), (c, d) => { Zone z = c.Zone; z.Size.x += d; return c.With(z); });
            Row(_zonePage, ref y, "Depth", c => M(c.Zone.Size.z), (c, d) => { Zone z = c.Zone; z.Size.z += d; return c.With(z); });
            Row(_zonePage, ref y, "Height", c => M(c.Zone.Size.y), (c, d) => { Zone z = c.Zone; z.Size.y += d; return c.With(z); });

            _parkPage = UiKit.Rect(w, "ParkPage", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(x0, -330f), new Vector2(1500f, 900f), new Vector2(0f, 1f));
            y = 0f;
            Section(_parkPage, ref y, "Position (from the pad)");
            Row(_parkPage, ref y, "Left / right", c => M(c.Park.x), (c, d) => c.With(c.Park + new Vector3(d, 0f, 0f), c.Heading));
            Row(_parkPage, ref y, "Back / front", c => M(c.Park.z), (c, d) => c.With(c.Park + new Vector3(0f, 0f, d), c.Heading));
            Row(_parkPage, ref y, "Down / up", c => M(c.Park.y), (c, d) => c.With(c.Park + new Vector3(0f, d, 0f), c.Heading));
            y -= 20f;
            Section(_parkPage, ref y, "Parked ship's heading");
            Choice(_parkPage, ref y, "Nose towards", c => HeadingName(c.Heading),
                d => Change(c => c.With(c.Park, (c.Heading + 1 + d + 5) % 5 - 1)));
            UiKit.Label(UiKit.Rect(_parkPage, "HeadingHint", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, y), new Vector2(0f, 110f), new Vector2(0.5f, 1f)),
                "Auto: the quarter turn nearest to the ship's heading as it comes in.",
                34, UiKit.TextDim, false, TextAlignmentOptions.Left);

            // bottom right: display setting, status, hint, buttons
            RectTransform bottom = UiKit.Rect(w, "Display", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(x0, 404f), new Vector2(1500f, 96f), new Vector2(0f, 0f));
            y = 0f;
            _display = Choice(bottom, ref y, "Show zones", null, d =>
            {
                TractorClient.Display = (ZoneDisplay)(((int)TractorClient.Display + d + 3) % 3);
                Refresh();
            });
            _status = UiKit.Label(UiKit.Rect(w, "Status", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(x0, 290f), new Vector2(1500f, 100f), new Vector2(0f, 0f)),
                "", 40, UiKit.Text, false, TextAlignmentOptions.Left);
            _hint = UiKit.Label(UiKit.Rect(w, "Hint", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(x0, 176f), new Vector2(1500f, 104f), new Vector2(0f, 0f)),
                "", 34, UiKit.TextDim, false, TextAlignmentOptions.TopLeft);

            Button(w, new Vector2(x0 + 360f, 70f), 720f, "Reset this tab", () =>
            {
                if (!Current(out _, out PadSpec spec)) return;
                Change(c => _tab == Tab.Zone ? c.With(PadConfig.DefaultZone(spec)) : c.With(Vector3.zero, PadConfig.AutoHeading));
            });
            Button(w, new Vector2(x0 + 1140f, 70f), 720f, "Close", () => ServiceLocator.GetService<MenuTracker>()?.CloseAllOpenMenus());
        }

        private static string M(float v) => $"{v:0.#} m";

        private static Image Tab_(RectTransform w, Vector2 pos, string text, Action click)
        {
            RectTransform b = UiKit.Rect(w, "Tab", new Vector2(0f, 1f), new Vector2(0f, 1f), pos, new Vector2(740f, 96f), new Vector2(0f, 1f));
            UiKit.TabButton(b, text, click, out Image fill, out _);
            return fill;
        }

        private static void Section(RectTransform w, ref float y, string text)
        {
            UiKit.Label(UiKit.Rect(w, "Section", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, y), new Vector2(1500f, 64f), new Vector2(0f, 1f)),
                text, 46, UiKit.Accent);
            y -= 80f;
        }

        private static void Row(RectTransform w, ref float y, string name, Func<PadConfig, string> get, Func<PadConfig, float, PadConfig> set)
        {
            RectTransform row = UiKit.Rect(w, "Row", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, y), new Vector2(0f, 96f), new Vector2(0.5f, 1f));
            UiKit.Label(UiKit.Rect(row, "Name", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(470f, 80f), new Vector2(0f, 0.5f)), name, 42, UiKit.Text);
            TextMeshProUGUI value = UiKit.Label(UiKit.Rect(row, "Value", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(480f, 0f), new Vector2(230f, 80f), new Vector2(0f, 0.5f)),
                "", 44, UiKit.Text, true, TextAlignmentOptions.Right);
            Values.Add((get, value));
            float[] steps = { -5f, -1f, 1f, 5f };
            for (int i = 0; i < steps.Length; i++)
            {
                float d = steps[i];
                RectTransform b = UiKit.Rect(row, "Step", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(760f + i * 150f, 0f), new Vector2(136f, 86f), new Vector2(0f, 0.5f));
                UiKit.CellButton(b, () => Change(c => set(c, d)), out _, out _);
                UiKit.Label(UiKit.Fill(b, "Text"), d > 0 ? $"+{d:0}" : $"{d:0}", 40, UiKit.Text, true, TextAlignmentOptions.Center);
            }
            y -= 106f;
        }

        /// <summary>A setting picked from a list: its value between ‹ and › buttons (step -1 or +1).</summary>
        private static TextMeshProUGUI Choice(RectTransform w, ref float y, string name, Func<PadConfig, string> get, Action<int> step)
        {
            RectTransform row = UiKit.Rect(w, "Choice", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, y), new Vector2(0f, 96f), new Vector2(0.5f, 1f));
            UiKit.Label(UiKit.Rect(row, "Name", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(600f, 80f), new Vector2(0f, 0.5f)), name, 42, UiKit.Text);
            RectTransform left = UiKit.Rect(row, "Prev", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(760f, 0f), new Vector2(136f, 86f), new Vector2(0f, 0.5f));
            UiKit.CellButton(left, () => step(-1), out _, out _);
            UiKit.Label(UiKit.Fill(left, "Text"), "<", 40, UiKit.Text, true, TextAlignmentOptions.Center);
            TextMeshProUGUI value = UiKit.Label(UiKit.Rect(row, "Value", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(906f, 0f), new Vector2(434f, 80f), new Vector2(0f, 0.5f)),
                "", 42, UiKit.Text, true, TextAlignmentOptions.Center);
            RectTransform right = UiKit.Rect(row, "Next", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(1350f, 0f), new Vector2(136f, 86f), new Vector2(0f, 0.5f));
            UiKit.CellButton(right, () => step(1), out _, out _);
            UiKit.Label(UiKit.Fill(right, "Text"), ">", 40, UiKit.Text, true, TextAlignmentOptions.Center);
            if (get != null) Values.Add((get, value));
            y -= 106f;
            return value;
        }

        private static void Button(RectTransform w, Vector2 pos, float width, string text, Action click)
        {
            RectTransform b = UiKit.Rect(w, "Button", new Vector2(0f, 0f), new Vector2(0f, 0f), pos, new Vector2(width, 96f), new Vector2(0.5f, 0f));
            UiKit.TabButton(b, text, click, out _, out _);
        }
    }
}
