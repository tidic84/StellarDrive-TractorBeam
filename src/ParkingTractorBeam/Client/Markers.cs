using System.Collections.Generic;
using ParkingTractorBeam.Docking;
using ParkingTractorBeam.Visual;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ParkingTractorBeam.Client
{
    /// <summary>
    /// Lines (and, if asked, faint glowing faces) in a pad's frame, under the carrier's visual object so they follow the
    /// ship models exactly. Built for one shape (its <see cref="Key"/>): a shape that changes is a new marker.
    /// "On top" markers are drawn through the hull, for the pad whose menu is open.
    /// </summary>
    internal sealed class Marker
    {
        private static readonly Dictionary<(Color, bool), Material> LineMaterials = new Dictionary<(Color, bool), Material>();
        private static readonly Dictionary<(Color, bool), Material> FaceMaterials = new Dictionary<(Color, bool), Material>();

        private readonly GameObject _go;
        private readonly List<LineRenderer> _lines = new List<LineRenderer>();
        private MeshRenderer _faces;
        private Mesh _mesh;
        private float _alpha;
        private readonly float _width;
        private Color _color = Color.clear;
        private bool _onTop;

        public readonly object Key;

        public bool Broken => _go == null;

        public Marker(Transform parent, Vector3 pos, Quaternion rot, object key, float width)
        {
            Key = key;
            _width = width;
            _go = new GameObject("ParkingTractorBeam.Marker");
            _go.transform.SetParent(parent, false);
            _go.transform.localPosition = pos;
            _go.transform.localRotation = rot;
        }

        public Marker Line(params Vector3[] pts)
        {
            var go = new GameObject("Line");
            go.transform.SetParent(_go.transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = false;
            l.widthMultiplier = _width;
            l.numCornerVertices = 0;
            l.shadowCastingMode = ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.positionCount = pts.Length;
            l.SetPositions(pts);
            _lines.Add(l);
            return this;
        }

        /// <summary>The 12 edges of a box (one line).</summary>
        public Marker Edges(Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + new Vector3(x * h.x, y * h.y, z * h.z);
            return Line(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), P(1, 1, -1),
                P(1, 1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1));
        }

        /// <summary>A rectangle on the floor (y) with an arrow along <paramref name="heading"/> if one is given.</summary>
        public Marker Footprint(Vector3 center, float width, float depth, Quaternion? heading)
        {
            float hx = width * 0.5f, hz = depth * 0.5f;
            Vector3 P(float x, float z) => center + new Vector3(x * hx, 0f, z * hz);
            Line(P(-1, -1), P(1, -1), P(1, 1), P(-1, 1), P(-1, -1));
            if (heading.HasValue)
            {
                Quaternion q = heading.Value;
                float l = Mathf.Min(hx, hz) * 0.8f, a = l * 0.35f;
                Line(center - q * new Vector3(0f, 0f, l * 0.6f), center + q * new Vector3(0f, 0f, l));
                Line(center + q * new Vector3(-a, 0f, l - a), center + q * new Vector3(0f, 0f, l), center + q * new Vector3(a, 0f, l - a));
            }
            else
            {
                float c = Mathf.Min(hx, hz) * 0.25f;
                Line(center + new Vector3(-c, 0f, 0f), center + new Vector3(c, 0f, 0f));
                Line(center + new Vector3(0f, 0f, -c), center + new Vector3(0f, 0f, c));
            }
            return this;
        }

        /// <summary>Faint faces on a box, seen from both sides.</summary>
        public Marker Faces(Vector3 center, Vector3 size, Quaternion rot, float alpha)
        {
            Vector3 h = size * 0.5f;
            var v = new List<Vector3>();
            var tris = new List<int>();
            // each face: 4 corners, two triangles each way
            Vector3[] n = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (Vector3 f in n)
            {
                Vector3 u = Mathf.Abs(f.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 w = Vector3.Cross(f, u);
                int o = v.Count;
                foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                    v.Add(center + rot * Vector3.Scale(f + u * a + w * b, h));
                tris.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3, o, o + 2, o + 1, o, o + 3, o + 2 });
            }
            _mesh = new Mesh { name = "ParkingTractorBeam.Faces" };
            _mesh.SetVertices(v);
            var uv = new Vector2[v.Count];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(0.5f, 0.5f);
            _mesh.uv = uv;
            _alpha = alpha;
            _mesh.SetTriangles(tris, 0);
            var go = new GameObject("Faces");
            go.transform.SetParent(_go.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _faces = go.AddComponent<MeshRenderer>();
            _faces.shadowCastingMode = ShadowCastingMode.Off;
            _faces.receiveShadows = false;
            return this;
        }

        public void Show(Color c, bool onTop)
        {
            if (_go == null || c == _color && onTop == _onTop) return;
            _color = c;
            _onTop = onTop;
            Material lm = Get(LineMaterials, c, onTop, () => Assets.Tint(c, 1.2f));
            // the spark shader adds the vertex colour to its own (and multiplies the alpha): black keeps the colour pure
            foreach (LineRenderer l in _lines)
            {
                l.sharedMaterial = lm;
                l.startColor = l.endColor = Color.black;
            }
            if (_faces != null)
            {
                var colors = new Color[_mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++) colors[i] = new Color(0f, 0f, 0f, _alpha);
                _mesh.colors = colors;
                _faces.sharedMaterial = Get(FaceMaterials, c, onTop, () => Assets.Tint(c, 1f));
            }
        }

        private static Material Get(Dictionary<(Color, bool), Material> cache, Color c, bool onTop, System.Func<Material> make)
        {
            if (cache.TryGetValue((c, onTop), out Material m) && m != null) return m;
            m = make();
            if (onTop)
            {
                if (m.HasProperty("_ZTest")) m.SetFloat("_ZTest", (float)CompareFunction.Always);
                m.renderQueue = (int)RenderQueue.Overlay;
            }
            return cache[(c, onTop)] = m;
        }

        public void Destroy()
        {
            if (_go != null) Object.Destroy(_go);
            if (_mesh != null) Object.Destroy(_mesh);
        }

        /// <summary>Line width for a shape of that size: thin up close, still visible on a big zone seen from afar.</summary>
        public static float WidthFor(Vector3 size, bool edited) => Mathf.Clamp(Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * (edited ? 0.005f : 0.003f), 0.06f, 0.3f);

        // ------------------------------------------------------------------ shapes

        /// <summary>A pad's landing zone.</summary>
        public static Marker Zone(Transform parent, Vector3 padPos, Quaternion padRot, Zone z, bool edited)
        {
            var m = new Marker(parent, padPos, padRot, ("zone", z.Bottom, z.Size, edited), WidthFor(z.Size, edited));
            m.Edges(z.Center + new Vector3(0f, 0.01f, 0f), z.Size - new Vector3(0f, 0.02f, 0f));
            if (edited) m.Faces(z.Center, z.Size, Quaternion.identity, 0.05f);
            return m;
        }

        /// <summary>
        /// A pad's parking spot: the footprint of the biggest ship the pad takes, with the heading it is set down with;
        /// in the menu, the whole space that ship may fill.
        /// </summary>
        public static Marker Park(Transform parent, Vector3 padPos, Quaternion padRot, PadSpec spec, PadConfig c, bool edited)
        {
            var size = new Vector3(spec.Width, spec.Height, spec.Width);
            var m = new Marker(parent, padPos, padRot, ("park", c.Park, c.Heading, edited), WidthFor(size, edited) * 0.8f);
            Quaternion? heading = c.Heading >= 0 ? Quaternion.Euler(0f, c.Heading * 90f, 0f) : (Quaternion?)null;
            m.Footprint(c.Park + new Vector3(0f, Pads.Gap, 0f), spec.Width, spec.Width, heading);
            if (edited)
            {
                Vector3 center = c.Park + new Vector3(0f, spec.Height * 0.5f, 0f);
                m.Edges(center, size);
                m.Faces(center, size, Quaternion.identity, 0.035f);
            }
            return m;
        }

        /// <summary>Where the piloted ship would be set down: its box at its landed pose, with an arrow along its nose.</summary>
        public static Marker Ghost(Transform parent, Vector3 pos, Quaternion rot, Bounds box)
        {
            var m = new Marker(parent, pos, rot, ("ghost", pos, rot, box.center, box.size), WidthFor(box.size, false));
            m.Edges(box.center, box.size);
            m.Footprint(new Vector3(box.center.x, box.min.y + 0.02f, box.center.z), box.size.x, box.size.z, Quaternion.identity);
            m.Faces(box.center, box.size, Quaternion.identity, 0.04f);
            return m;
        }
    }
}
