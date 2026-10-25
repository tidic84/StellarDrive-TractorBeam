using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Players.Interface.Model;
using Ships;
using Ships.Grid.Model;
using Ships.Interface.Model.Parts;
using TMPro;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Docking;
using ParkingTractorBeam.Net;
using ParkingTractorBeam.Visual;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ParkingTractorBeam.Client
{
    /// <summary>When the landing zones and parking spots are drawn (the pad whose menu is open always shows its own).</summary>
    internal enum ZoneDisplay
    {
        Always,
        Piloting,
        Never,
    }

    /// <summary>
    /// The pilot's side: the landing key, the prompt and the host's answers on screen, the outline of the landing
    /// zones nearby with their parking spots, where the piloted ship would be set down, and the beams the host announces.
    /// </summary>
    internal static class TractorClient
    {
        private static MelonPreferences_Entry<string> _key, _display;
        private static InputControl _control;
        private static string _controlPath;
        private static bool _wasPressed;
        private static float _nextRequest;

        private static Canvas _canvas;
        private static TextMeshProUGUI _prompt, _message;
        private static float _messageUntil;

        private static readonly Dictionary<uint, Beam> Beams = new Dictionary<uint, Beam>();
        private static readonly Dictionary<(uint, ushort, int), Marker> Markers = new Dictionary<(uint, ushort, int), Marker>();
        private const int ZoneMarker = 0, ParkMarker = 1, GhostMarker = 2;

        public static void Init(MelonPreferences_Category prefs)
        {
            _key = prefs.CreateEntry("LandKey", "<Keyboard>/l", "Landing key", "Input control path of the key that asks the tractor beam to land or launch the ship you pilot.");
            _display = prefs.CreateEntry("ShowZones", nameof(ZoneDisplay.Always), "Show zones",
                "When the landing zones and parking spots are drawn: Always (near them), Piloting (only at the controls of a ship) or Never. Also set from a pad's menu.");
            TractorNet.OnClient(Op.Result, OnResult);
            TractorNet.OnClient(Op.Beam, OnBeam);
        }

        public static void Reset()
        {
            foreach (Beam b in Beams.Values) b.Destroy();
            Beams.Clear();
            foreach (Marker m in Markers.Values) m.Destroy();
            Markers.Clear();
            if (_canvas != null) Object.Destroy(_canvas.gameObject);
            _canvas = null;
            _control = null;
        }

        public static ZoneDisplay Display
        {
            get => System.Enum.TryParse(_display.Value, true, out ZoneDisplay d) ? d : ZoneDisplay.Always;
            set
            {
                _display.Value = value.ToString();
                MelonPreferences.Save();
            }
        }

        // ------------------------------------------------------------------ network

        private static void OnResult(BinaryReader r)
        {
            var v = (Verdict)r.ReadByte();
            Show(Text(v), v == Verdict.Landing || v == Verdict.Launching ? UiColors.Good : UiColors.Warning);
        }

        private static void OnBeam(BinaryReader r)
        {
            uint child = r.ReadUInt32(), parent = r.ReadUInt32();
            ushort pad = r.ReadUInt16();
            byte kind = r.ReadByte();
            r.ReadSingle();
            if (kind == 0)
            {
                if (Beams.TryGetValue(child, out Beam b)) b.Release();
                return;
            }
            if (Beams.TryGetValue(child, out Beam old)) old.Destroy();
            Beams[child] = new Beam(child, parent, pad, kind == 2);
        }

        public static string Text(Verdict v)
        {
            switch (v)
            {
                case Verdict.Landing: return "Tractor beam locked: landing.";
                case Verdict.Launching: return "Tractor beam: launching.";
                case Verdict.NotPiloting: return "Take the controls of the ship to ask for landing.";
                case Verdict.Busy: return "The tractor beam is already holding this ship.";
                case Verdict.NoZone: return "No landing zone here: fly into a tractor beam's zone.";
                case Verdict.NotInside: return "Get further into the zone: the ship's centre must be inside it, and it may stick out only a little.";
                case Verdict.TooFast: return $"Too fast: slow down under {Tractor.MaxRelativeSpeed:0} m/s relative to the carrier.";
                case Verdict.Spinning: return "Steady the ship: it is spinning too fast.";
                case Verdict.TooBig: return "This ship is too big for this pad.";
                case Verdict.PadBusy: return "The pad is already taken.";
                case Verdict.Blocked: return "Path blocked: something is in the way between the ship and the pad.";
                case Verdict.Jointed: return "This ship is already joined to another one (docking door or rotor).";
                case Verdict.Anchored: return "Raise the anchor before landing.";
                case Verdict.Heavier: return "The carrier must be heavier than the ship to land.";
                case Verdict.NoRoom: return "Not enough room at the parking spot for this ship: it would touch the hull.";
                default: return v.ToString();
            }
        }

        // ------------------------------------------------------------------ every frame

        public static void Update()
        {
            EnsureHud();
            uint piloted = Piloted();
            UpdatePrompt(piloted);
            if (piloted != 0 && Pressed() && Time.unscaledTime >= _nextRequest)
            {
                _nextRequest = Time.unscaledTime + 0.5f;
                TractorNet.SendToServer(Op.Request, null);
            }
            UpdateOutlines(piloted);
            List<uint> gone = null;
            foreach (var kv in Beams)
                if (!kv.Value.Update())
                    (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
                foreach (uint id in gone)
                {
                    Beams[id].Destroy();
                    Beams.Remove(id);
                }
            if (_message != null && _message.gameObject.activeSelf && Time.unscaledTime > _messageUntil) _message.gameObject.SetActive(false);
        }

        private static uint Piloted()
        {
            if (Svc.PlayersClient == null || !Svc.PlayersClient.TryGetLocalPlayerState(out PlayerState state)) return 0;
            return state.Type == PlayerStateType.Piloting ? state.EntityId : 0;
        }

        private static bool Pressed()
        {
            string path = _key.Value;
            if (string.IsNullOrEmpty(path)) return false;
            if (_control == null || _controlPath != path || _control.device == null || !_control.device.added)
            {
                _control = InputSystem.FindControl(path);
                _controlPath = path;
            }
            bool down = _control is UnityEngine.InputSystem.Controls.ButtonControl b && b.isPressed;
            bool edge = down && !_wasPressed;
            _wasPressed = down;
            return edge;
        }

        private static string KeyName()
        {
            string p = _key.Value ?? "";
            int i = p.LastIndexOf('/');
            return (i >= 0 ? p.Substring(i + 1) : p).ToUpperInvariant();
        }

        private static void UpdatePrompt(uint piloted)
        {
            if (_prompt == null) return;
            string text = null;
            if (piloted != 0)
            {
                if (Docks.Known.ContainsKey(piloted)) text = $"[{KeyName()}] Launch from the tractor beam";
                else if (!Beams.ContainsKey(piloted) && FindZone(piloted, out bool inside, out PadSpec spec))
                    text = inside
                        ? $"[{KeyName()}] Request landing ({spec.Name} tractor beam)"
                        : "Landing zone: get further in";
            }
            _prompt.gameObject.SetActive(text != null);
            if (text != null) _prompt.text = text;
        }

        /// <summary>
        /// The free pad zone around the piloted ship's centre (client estimate from the hull box; the host decides).
        /// A pad that holds a ship, or is busy with one, has no zone.
        /// </summary>
        private static bool FindZone(uint shipId, out bool inside, out PadSpec spec) => FindZone(shipId, out inside, out spec, out _);

        private static bool FindZone(uint shipId, out bool inside, out PadSpec spec, out PartContext found)
        {
            inside = false;
            spec = null;
            found = default;
            var ships = Svc.ShipsClient;
            if (ships == null || !ships.TryGetTrackedShip(shipId, out TrackedShipClient ship) || !ship.IsRigidbodyLoaded) return false;
            Transform ct = ship.ShipRigidBody.transform;
            CoordBounds hb = ship.GetHullCoordBounds();
            var box = new Bounds(hb.Center, hb.Extents * 2f);
            Vector3 center = ct.TransformPoint(box.center);
            float best = float.MaxValue;
            foreach (TrackedShipClient other in ships.Ships)
            {
                if (other == ship || !other.IsRigidbodyLoaded) continue;
                Transform pt = other.ShipRigidBody.transform;
                foreach (StatefulPart p in Pads.PadsOf(other))
                {
                    if (PadBusy(other.Id, p.Id)) continue;
                    var (pp, pr) = Pads.Frame(p);
                    Quaternion inv = Quaternion.Inverse(pt.rotation * pr);
                    Vector3 padWorld = pt.TransformPoint(pp);
                    Vector3 local = inv * (center - padWorld);
                    Zone z = Pads.ZoneOf(other.Id, p);
                    float d = (local - z.Center).sqrMagnitude;
                    if (!Pads.InZone(z, local, 2f) || d >= best) continue;
                    best = d;
                    spec = Pads.Spec(p);
                    found = new PartContext(other.Id, p.Id);
                    // as the host checks it: the centre in the zone, sticking out by no more than the overhang allowed
                    inside = Pads.InZone(z, local);
                    float overhang = Pads.Overhang(z);
                    foreach (Vector3 c in Tractor.Corners(box))
                        if (inside && !Pads.InZone(z, inv * (ct.TransformPoint(c) - padWorld), overhang))
                        {
                            inside = false;
                            break;
                        }
                }
            }
            return spec != null;
        }

        /// <summary>
        /// Zone outlines of the free pads near the camera with their parking spots (green when the piloted ship is
        /// inside enough to be caught), and the box where the piloted ship would be set down; as the display setting says. The pad
        /// whose menu is open always shows its zone and the space its parking spot takes, through the hull (orange while
        /// the pad is taken: the zone is off).
        /// </summary>
        private static void UpdateOutlines(uint piloted)
        {
            var ships = Svc.ShipsClient;
            Camera cam = Camera.main;
            var seen = new HashSet<(uint, ushort, int)>();
            ZoneDisplay display = Display;
            bool showAll = display == ZoneDisplay.Always || display == ZoneDisplay.Piloting && piloted != 0;
            if (ships != null && cam != null)
            {
                PartContext target = default;
                bool inside = false, found = false;
                if (piloted != 0 && showAll) found = FindZone(piloted, out inside, out _, out target);
                PartContext? editing = PadMenu.Editing;
                Vector3 eye = cam.transform.position;
                foreach (TrackedShipClient ship in ships.Ships)
                {
                    if (!ship.IsVisualLoaded || ship.VisualObject == null) continue;
                    Transform vt = ship.VisualObject.transform;
                    foreach (StatefulPart p in Pads.PadsOf(ship))
                    {
                        bool edited = editing.HasValue && editing.Value.ShipId == ship.Id && editing.Value.PartId == p.Id;
                        bool busy = PadBusy(ship.Id, p.Id);
                        PadConfig c = PadConfigs.Get(ship.Id, p);
                        Zone z = c.Zone;
                        var (pp, pr) = Pads.Frame(p);
                        Vector3 local = Quaternion.Inverse(vt.rotation * pr) * (eye - vt.TransformPoint(pp));
                        if (!edited && (!showAll || busy || !Pads.InZone(z, local, 25f))) continue;
                        bool here = found && target.ShipId == ship.Id && target.PartId == p.Id;
                        Color zc = edited && busy ? UiColors.Warning : edited ? UiColors.Edit : here && inside ? UiColors.Good : UiColors.Accent;
                        Keep(seen, (ship.Id, p.Id, ZoneMarker), ("zone", z.Bottom, z.Size, edited), () => Marker.Zone(vt, pp, pr, z, edited), zc, edited);
                        PadSpec spec = Pads.Spec(p);
                        Keep(seen, (ship.Id, p.Id, ParkMarker), ("park", c.Park, c.Heading, edited), () => Marker.Park(vt, pp, pr, spec, c, edited),
                            edited && busy ? UiColors.Warning : UiColors.Park, edited);
                        if (here && Ghost(piloted, ship, vt, pp, pr, c, out Vector3 gp, out Quaternion gr, out Bounds box))
                            Keep(seen, (ship.Id, p.Id, GhostMarker), ("ghost", gp, gr, box.center, box.size), () => Marker.Ghost(vt, gp, gr, box),
                                inside ? UiColors.Good : UiColors.Park, false);
                    }
                }
            }
            List<(uint, ushort, int)> gone = null;
            foreach (var key in Markers.Keys)
                if (!seen.Contains(key))
                    (gone ??= new List<(uint, ushort, int)>()).Add(key);
            if (gone != null)
                foreach (var key in gone)
                {
                    Markers[key].Destroy();
                    Markers.Remove(key);
                }
        }

        private static void Keep(HashSet<(uint, ushort, int)> seen, (uint, ushort, int) slot, object shape, System.Func<Marker> make, Color color, bool onTop)
        {
            seen.Add(slot);
            if (!Markers.TryGetValue(slot, out Marker m) || m.Broken || !Equals(m.Key, shape))
            {
                m?.Destroy();
                Markers[slot] = m = make();
            }
            m.Show(color, onTop);
        }

        /// <summary>
        /// Where the piloted ship would be set down on that pad (as the host computes it, from the hull box), in the
        /// carrier's visual frame.
        /// </summary>
        private static bool Ghost(uint piloted, TrackedShipClient carrier, Transform vt, Vector3 pp, Quaternion pr, PadConfig c, out Vector3 pos, out Quaternion rot, out Bounds box)
        {
            pos = default;
            rot = default;
            box = default;
            if (!Svc.ShipsClient.TryGetTrackedShip(piloted, out TrackedShipClient ship) || !ship.IsVisualLoaded || ship.VisualObject == null) return false;
            CoordBounds hb = ship.GetHullCoordBounds();
            box = new Bounds(hb.Center, hb.Extents * 2f);
            Quaternion shipInPad = Quaternion.Inverse(vt.rotation * pr) * ship.VisualObject.transform.rotation;
            var (lp, lr) = Pads.Landed(c, box, shipInPad);
            pos = pp + pr * lp;
            rot = pr * lr;
            return true;
        }

        public static bool PadBusy(uint parent, ushort pad)
        {
            foreach (var d in Docks.Known.Values)
                if (d.Parent == parent && d.Pad == pad) return true;
            foreach (Beam b in Beams.Values)
                if (b.Parent == parent && b.Pad == pad) return true;
            return false;
        }

        // ------------------------------------------------------------------ HUD

        private static void Show(string text, Color color)
        {
            EnsureHud();
            if (_message == null) return;
            _message.text = text;
            _message.color = color;
            _message.gameObject.SetActive(true);
            _messageUntil = Time.unscaledTime + 4f;
        }

        private static void EnsureHud()
        {
            if (_canvas != null) return;
            _canvas = UiKit.MakeCanvas("ParkingTractorBeam.Hud", 40);
            RectTransform root = (RectTransform)_canvas.transform;
            _prompt = UiKit.Label(UiKit.Rect(root, "Prompt", new Vector2(0.5f, 0.7f), new Vector2(0.5f, 0.7f), new Vector2(0f, 0f), new Vector2(2400f, 80f)),
                "", 46f, UiColors.Text, true, TextAlignmentOptions.Center);
            _message = UiKit.Label(UiKit.Rect(root, "Message", new Vector2(0.5f, 0.7f), new Vector2(0.5f, 0.7f), new Vector2(0f, 90f), new Vector2(2400f, 80f)),
                "", 42f, UiColors.Text, true, TextAlignmentOptions.Center);
            _prompt.gameObject.SetActive(false);
            _message.gameObject.SetActive(false);
            Outline(_prompt);
            Outline(_message);
        }

        private static void Outline(TextMeshProUGUI t)
        {
            t.outlineWidth = 0.18f;
            t.outlineColor = new Color32(4, 16, 24, 220);
        }
    }

    internal static class UiColors
    {
        public static readonly Color Text = new Color(0.95f, 0.95f, 0.95f, 1f);
        public static readonly Color Accent = new Color(0.35f, 0.75f, 1f, 1f);
        public static readonly Color Warning = new Color(1f, 0.55f, 0.32f, 1f);
        public static readonly Color Good = new Color(0.45f, 0.9f, 0.55f, 1f);
        public static readonly Color Edit = new Color(1f, 0.85f, 0.3f, 1f);
        public static readonly Color Park = new Color(0.78f, 0.5f, 1f, 1f);
    }
}
