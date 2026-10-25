using System.Collections.Generic;
using Ships;
using Ships.Grid.Model;
using Ships.Interface.Model.Parts;
using ParkingTractorBeam.Core;
using ParkingTractorBeam.Docking;
using ParkingTractorBeam.Visual;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ParkingTractorBeam.Client
{
    /// <summary>
    /// The beam itself: a translucent cone from the pad's emitter to the bottom of the held ship, with bright bands
    /// running along it (down to the pad when landing, up when launching). It hangs under the carrier's visual object,
    /// so it follows the ship models exactly.
    /// </summary>
    internal sealed class Beam
    {
        private const int Around = 28, Along = 18;
        private static readonly Color Tint = new Color(0.35f, 0.8f, 1f);
        private static Material _material;

        public readonly uint Child, Parent;
        public readonly ushort Pad;
        private readonly bool _launch;
        private readonly float _born;
        private float _releasedAt = -1f;
        private GameObject _go;
        private Mesh _mesh;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();

        public Beam(uint child, uint parent, ushort pad, bool launch)
        {
            Child = child;
            Parent = parent;
            Pad = pad;
            _launch = launch;
            _born = Time.time;
        }

        private static Material Material => _material != null ? _material : (_material = Assets.Glow(Tint, 2f));

        public void Release()
        {
            if (_releasedAt < 0f) _releasedAt = Time.time;
        }

        /// <summary>False once it has faded out (or its ships are gone).</summary>
        public bool Update()
        {
            float fade = Mathf.Clamp01((Time.time - _born) / 0.4f);
            if (_releasedAt >= 0f) fade *= 1f - Mathf.Clamp01((Time.time - _releasedAt) / 0.7f);
            if (_releasedAt >= 0f && fade <= 0f) return false;
            var ships = Svc.ShipsClient;
            if (ships == null || !ships.TryGetTrackedShip(Parent, out TrackedShipClient carrier) || !ships.TryGetTrackedShip(Child, out TrackedShipClient held)) return false;
            if (!carrier.IsVisualLoaded || carrier.VisualObject == null || !held.IsVisualLoaded || held.VisualObject == null) return _releasedAt < 0f;
            if (!carrier.TryGetStatefulPart(Pad, out StatefulPart pad)) return false;
            Transform vt = carrier.VisualObject.transform;
            if (_go == null || _go.transform.parent != vt) Build(vt);

            var (pp, pr) = Pads.Frame(pad);
            CoordBounds hb = held.GetHullCoordBounds();
            Vector3 c = hb.Center, e = hb.Extents;
            Vector3 top = vt.InverseTransformPoint(held.VisualObject.transform.TransformPoint(new Vector3(c.x, c.y - e.y, c.z)));
            float r1 = Mathf.Clamp(Mathf.Max(e.x, e.z) * 0.6f, 0.6f, 20f);
            Vector3 axis = top - pp;
            float length = axis.magnitude;
            if (length < 0.05f) axis = pr * Vector3.up;
            Quaternion frame = Quaternion.LookRotation(axis.normalized, Mathf.Abs(Vector3.Dot(axis.normalized, pr * Vector3.forward)) > 0.95f ? pr * Vector3.up : pr * Vector3.forward);

            _v.Clear();
            _c.Clear();
            float flow = Time.time * (_launch ? 1.6f : -1.6f);
            Layer(pp, frame, length, 0.32f, r1, 0.16f * fade, flow);
            Layer(pp, frame, length, 0.14f, r1 * 0.35f, 0.32f * fade, flow * 1.3f);
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.RecalculateBounds();
            return true;
        }

        private void Layer(Vector3 origin, Quaternion frame, float length, float r0, float r1, float alpha, float flow)
        {
            for (int j = 0; j < Along; j++)
            {
                float t = j / (float)(Along - 1);
                float r = Mathf.Lerp(r0, r1, t * t * 0.4f + t * 0.6f);
                // soft at both ends, bands sliding along the beam
                float ends = Mathf.SmoothStep(0f, 1f, t * 6f) * (1f - t * t * 0.6f);
                float band = Mathf.Pow(Mathf.Max(0f, Mathf.Cos((t * 3.5f + flow) * Mathf.PI * 2f)), 6f);
                float a = alpha * ends * (0.55f + 1.4f * band);
                for (int i = 0; i < Around; i++)
                {
                    float ang = i / (float)Around * Mathf.PI * 2f;
                    _v.Add(origin + frame * new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, t * length));
                    _c.Add(new Color(Tint.r * a, Tint.g * a, Tint.b * a, a));
                }
            }
        }

        private void Build(Transform parent)
        {
            Destroy();
            _go = new GameObject("ParkingTractorBeam.Beam");
            _go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "ParkingTractorBeam.Beam" };
            _mesh.MarkDynamic();
            var verts = new Vector3[Around * Along * 2];
            var uv = new Vector2[verts.Length];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(0.5f, 0.5f);
            _mesh.vertices = verts;
            _mesh.uv = uv;
            var tris = new List<int>();
            for (int layer = 0; layer < 2; layer++)
            {
                int o = layer * Around * Along;
                for (int j = 0; j + 1 < Along; j++)
                    for (int i = 0; i < Around; i++)
                    {
                        int a = o + j * Around + i, b = o + j * Around + (i + 1) % Around, c = a + Around, d = b + Around;
                        // both sides: seen from outside and from the cockpit inside the beam
                        tris.AddRange(new[] { a, c, b, b, c, d, a, b, c, b, d, c });
                    }
            }
            _mesh.SetTriangles(tris, 0);
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = _go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        public void Destroy()
        {
            if (_go != null) Object.Destroy(_go);
            if (_mesh != null) Object.Destroy(_mesh);
            _go = null;
            _mesh = null;
        }
    }
}
