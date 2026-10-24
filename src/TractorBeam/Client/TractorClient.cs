using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Players.Interface.Model;
using Ships;
using Ships.Grid.Model;
using Ships.Interface.Model.Parts;
using TMPro;
using TractorBeam.Core;
using TractorBeam.Docking;
using TractorBeam.Net;
using TractorBeam.Visual;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace TractorBeam.Client
{
    /// <summary>
    /// The pilot's side: the landing key, the prompt and the host's answers on screen, the outline of the landing
    /// zones nearby, and the beams the host announces.
    /// </summary>
    internal static class TractorClient
    {
        private static MelonPreferences_Entry<string> _key;
        private static InputControl _control;
        private static string _controlPath;
        private static bool _wasPressed;
        private static float _nextRequest;

        private static Canvas _canvas;
        private static TextMeshProUGUI _prompt, _message;
        private static float _messageUntil;

        private static readonly Dictionary<uint, Beam> Beams = new Dictionary<uint, Beam>();
        private static readonly Dictionary<(uint, ushort), ZoneOutline> Outlines = new Dictionary<(uint, ushort), ZoneOutline>();

        public static void Init(MelonPreferences_Category prefs)
        {
            _key = prefs.CreateEntry("LandKey", "<Keyboard>/l", "Landing key", "Input control path of the key that asks the tractor beam to land or launch the ship you pilot.");
            TractorNet.OnClient(Op.Result, OnResult);
            TractorNet.OnClient(Op.Beam, OnBeam);
        }

        public static void Reset()
        {
            foreach (Beam b in Beams.Values) b.Destroy();
            Beams.Clear();
            foreach (ZoneOutline o in Outlines.Values) o.Destroy();
            Outlines.Clear();
            if (_canvas != null) Object.Destroy(_canvas.gameObject);
            _canvas = null;
            _control = null;
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
                case Verdict.Landing: return Loc.T("Rayon tracteur verrouillé : appontage en cours.", "Tractor beam locked: landing.");
                case Verdict.Launching: return Loc.T("Rayon tracteur : décollage.", "Tractor beam: launching.");
                case Verdict.NotPiloting: return Loc.T("Prenez les commandes du vaisseau pour demander l'appontage.", "Take the controls of the ship to ask for landing.");
                case Verdict.Busy: return Loc.T("Le rayon tracteur tient déjà ce vaisseau.", "The tractor beam is already holding this ship.");
                case Verdict.NoZone: return Loc.T("Aucune zone d'appontage ici : entrez dans la zone d'un rayon tracteur.", "No landing zone here: fly into a tractor beam's zone.");
                case Verdict.NotInside: return Loc.T("Le vaisseau doit être entièrement dans la zone.", "The whole ship must be inside the zone.");
                case Verdict.TooFast: return Loc.T($"Trop rapide : ralentissez sous {Tractor.MaxRelativeSpeed:0} m/s par rapport au porteur.", $"Too fast: slow down under {Tractor.MaxRelativeSpeed:0} m/s relative to the carrier.");
                case Verdict.Spinning: return Loc.T("Stabilisez le vaisseau : il tourne trop vite.", "Steady the ship: it is spinning too fast.");
                case Verdict.TooBig: return Loc.T("Ce vaisseau est trop grand pour cette plateforme.", "This ship is too big for this pad.");
                case Verdict.PadBusy: return Loc.T("La plateforme est déjà occupée.", "The pad is already taken.");
                case Verdict.Blocked: return Loc.T("Passage encombré : quelque chose gêne entre le vaisseau et la plateforme.", "Path blocked: something is in the way between the ship and the pad.");
                case Verdict.Jointed: return Loc.T("Ce vaisseau est déjà amarré à un autre (porte d'amarrage ou rotor).", "This ship is already joined to another one (docking door or rotor).");
                case Verdict.Anchored: return Loc.T("Levez l'ancre avant l'appontage.", "Raise the anchor before landing.");
                case Verdict.Heavier: return Loc.T("Le porteur doit être plus lourd que le vaisseau à poser.", "The carrier must be heavier than the ship to land.");
                case Verdict.NoRoom: return Loc.T("Pas assez de place sur la plateforme pour ce vaisseau : il toucherait la coque.", "Not enough room on the pad for this ship: it would touch the hull.");
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
                if (Docks.Known.ContainsKey(piloted)) text = Loc.T($"[{KeyName()}] Décoller du rayon tracteur", $"[{KeyName()}] Launch from the tractor beam");
                else if (!Beams.ContainsKey(piloted) && FindZone(piloted, out bool inside, out PadSpec spec))
                    text = inside
                        ? Loc.T($"[{KeyName()}] Demander l'appontage (rayon tracteur {spec.Fr})", $"[{KeyName()}] Request landing ({spec.En} tractor beam)")
                        : Loc.T("Zone d'appontage : entrez-y entièrement", "Landing zone: get the whole ship inside");
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
                    inside = true;
                    foreach (Vector3 c in Tractor.Corners(box))
                        if (!Pads.InZone(z, inv * (ct.TransformPoint(c) - padWorld), 0.05f))
                        {
                            inside = false;
                            break;
                        }
                }
            }
            return spec != null;
        }

        /// <summary>
        /// Zone outlines of the free pads near the camera: green when the piloted ship is entirely inside. The pad whose
        /// menu is open always shows its zone (orange while the pad is taken: the zone is off).
        /// </summary>
        private static void UpdateOutlines(uint piloted)
        {
            var ships = Svc.ShipsClient;
            Camera cam = Camera.main;
            var seen = new HashSet<(uint, ushort)>();
            if (ships != null && cam != null)
            {
                PartContext target = default;
                bool inside = false;
                if (piloted != 0) FindZone(piloted, out inside, out _, out target);
                PartContext? editing = PadMenu.Editing;
                Vector3 eye = cam.transform.position;
                foreach (TrackedShipClient ship in ships.Ships)
                {
                    if (!ship.IsVisualLoaded || ship.VisualObject == null) continue;
                    Transform vt = ship.VisualObject.transform;
                    foreach (StatefulPart p in Pads.PadsOf(ship))
                    {
                        var key = (ship.Id, p.Id);
                        bool edited = editing.HasValue && editing.Value.ShipId == ship.Id && editing.Value.PartId == p.Id;
                        bool busy = PadBusy(ship.Id, p.Id);
                        Zone z = Pads.ZoneOf(ship.Id, p);
                        var (pp, pr) = Pads.Frame(p);
                        Vector3 local = Quaternion.Inverse(vt.rotation * pr) * (eye - vt.TransformPoint(pp));
                        if (!edited && (busy || !Pads.InZone(z, local, 25f))) continue;
                        seen.Add(key);
                        if (!Outlines.TryGetValue(key, out ZoneOutline o) || o.Broken || !o.Shows(z))
                        {
                            o?.Destroy();
                            Outlines[key] = o = new ZoneOutline(vt, pp, pr, z);
                        }
                        bool here = inside && target.ShipId == ship.Id && target.PartId == p.Id;
                        o.SetColor(edited && busy ? UiColors.Warning : edited ? UiColors.Edit : here ? UiColors.Good : UiColors.Accent);
                    }
                }
            }
            List<(uint, ushort)> gone = null;
            foreach (var key in Outlines.Keys)
                if (!seen.Contains(key))
                    (gone ??= new List<(uint, ushort)>()).Add(key);
            if (gone != null)
                foreach (var key in gone)
                {
                    Outlines[key].Destroy();
                    Outlines.Remove(key);
                }
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
            _canvas = UiKit.MakeCanvas("TractorBeam.Hud", 40);
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
    }
}
