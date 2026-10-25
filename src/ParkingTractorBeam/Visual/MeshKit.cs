using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ParkingTractorBeam.Visual
{
    /// <summary>
    /// A surface finish: an area of the game's hand-drawn tool atlas (ink, flat or hatched) and a colour of the
    /// game's ship palette (the PlanetObject shader multiplies the atlas by the palette colour picked by uv2).
    /// </summary>
    internal enum Shade
    {
        Black,
        Dark,
        Light,
        White,
        HatchDark,
        HatchMid,
        HatchLight,
        Grey,
        Yellow,
        Blue,
        Orange,
        Red,
        Green,
    }

    /// <summary>
    /// Builds low-poly meshes out of boxes, prisms and cylinders, textured like the game's own tools.
    /// </summary>
    internal sealed class MeshKit
    {
        private const float TexelsPerMetre = 0.55f; // uv units per metre on faces

        // Areas of the 2048x2048 MiningLaser albedo (uv space).
        private static readonly Rect Ink = Rect.MinMaxRect(0.445f, 0.905f, 0.62f, 0.99f);
        private static readonly Rect Flat = Rect.MinMaxRect(0.163f, 0.505f, 0.305f, 0.558f);
        private static readonly Rect Bright = Rect.MinMaxRect(0.725f, 0.787f, 0.804f, 0.871f);
        private static readonly Rect HatchA = Rect.MinMaxRect(0.092f, 0.237f, 0.223f, 0.308f);
        private static readonly Rect HatchB = Rect.MinMaxRect(0.487f, 0.707f, 0.718f, 0.738f);
        private static readonly Rect HatchC = Rect.MinMaxRect(0.412f, 0.777f, 0.458f, 0.848f);

        // Palette columns of ShipColorPalette (row 0): 1 grey .7, 2 dark .3, 3 light .9, 4 red, 5 yellow, 6 green, 7 blue, 10 orange.
        private static readonly Dictionary<Shade, (Rect area, int palette)> Finishes = new Dictionary<Shade, (Rect, int)>
        {
            [Shade.Black] = (Ink, 2),
            [Shade.Dark] = (Flat, 2),
            [Shade.Light] = (Flat, 3),
            [Shade.White] = (Bright, 3),
            [Shade.HatchDark] = (HatchA, 2),
            [Shade.HatchMid] = (HatchB, 1),
            [Shade.HatchLight] = (HatchC, 3),
            [Shade.Grey] = (Flat, 1),
            [Shade.Yellow] = (Bright, 5),
            [Shade.Blue] = (Bright, 7),
            [Shade.Orange] = (Bright, 10),
            [Shade.Red] = (Bright, 4),
            [Shade.Green] = (Bright, 6),
        };

        /// <summary>The same colour without hatching (hatching looks wrong on small fan-shaped caps).</summary>
        public static Shade FlatOf(Shade s) => s switch
        {
            Shade.HatchDark => Shade.Dark,
            Shade.HatchMid => Shade.Grey,
            Shade.HatchLight => Shade.Light,
            _ => s,
        };

        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Vector3> _n = new List<Vector3>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector2> _uv2 = new List<Vector2>();
        private readonly List<int> _t = new List<int>();

        public Matrix4x4 Transform = Matrix4x4.identity;

        public MeshKit At(Vector3 pos, Vector3 euler = default)
        {
            Transform = Matrix4x4.TRS(pos, Quaternion.Euler(euler), Vector3.one);
            return this;
        }

        /// <summary>Runs <paramref name="body"/> with the transform moved by (pos, euler).</summary>
        public void With(Vector3 pos, Vector3 euler, System.Action body)
        {
            Matrix4x4 saved = Transform;
            Transform = saved * Matrix4x4.TRS(pos, Quaternion.Euler(euler), Vector3.one);
            body();
            Transform = saved;
        }

        /// <summary>Axis-aligned box (in the current transform), each face its own flat-shaded quad.</summary>
        public MeshKit Box(Vector3 center, Vector3 size, Shade shade, Shade? sides = null)
        {
            Vector3 h = size * 0.5f;
            Vector3[] c =
            {
                center + new Vector3(-h.x, -h.y, -h.z), center + new Vector3(h.x, -h.y, -h.z),
                center + new Vector3(h.x, h.y, -h.z), center + new Vector3(-h.x, h.y, -h.z),
                center + new Vector3(-h.x, -h.y, h.z), center + new Vector3(h.x, -h.y, h.z),
                center + new Vector3(h.x, h.y, h.z), center + new Vector3(-h.x, h.y, h.z),
            };
            Shade s = sides ?? shade;
            Quad(c[3], c[2], c[1], c[0], shade); // back (-z)
            Quad(c[4], c[5], c[6], c[7], shade); // front (+z)
            Quad(c[7], c[6], c[2], c[3], shade); // top
            Quad(c[0], c[1], c[5], c[4], s);     // bottom
            Quad(c[0], c[4], c[7], c[3], s);     // left
            Quad(c[5], c[1], c[2], c[6], s);     // right
            return this;
        }

        /// <summary>Box with its top edges chamfered, which reads better with the game's edge outlines.</summary>
        public MeshKit Bevel(Vector3 center, Vector3 size, float bevel, Shade shade, Shade? sides = null)
        {
            bevel = Mathf.Min(bevel, size.x * 0.45f, size.y * 0.45f);
            Vector3 h = size * 0.5f;
            float zf = center.z + h.z, zb = center.z - h.z;
            // octagon-ish profile in x/y, extruded along z
            var p = new[]
            {
                new Vector2(-h.x + bevel, -h.y), new Vector2(h.x - bevel, -h.y),
                new Vector2(h.x, -h.y + bevel), new Vector2(h.x, h.y - bevel),
                new Vector2(h.x - bevel, h.y), new Vector2(-h.x + bevel, h.y),
                new Vector2(-h.x, h.y - bevel), new Vector2(-h.x, -h.y + bevel),
            };
            Prism(p.Select(q => q + new Vector2(center.x, center.y)).ToArray(), zb, zf, FlatOf(shade), sides ?? shade);
            return this;
        }

        /// <summary>Extrudes a convex polygon (counter-clockwise in x/y) between z0 and z1.</summary>
        public MeshKit Prism(Vector2[] poly, float z0, float z1, Shade caps, Shade sides)
        {
            int n = poly.Length;
            // caps as fans
            Vector3 c0 = Vector3.zero;
            foreach (Vector2 q in poly) c0 += new Vector3(q.x, q.y, 0);
            c0 /= n;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Tri(new Vector3(c0.x, c0.y, z1), new Vector3(a.x, a.y, z1), new Vector3(b.x, b.y, z1), caps);
                Tri(new Vector3(c0.x, c0.y, z0), new Vector3(b.x, b.y, z0), new Vector3(a.x, a.y, z0), caps);
                Quad(new Vector3(a.x, a.y, z0), new Vector3(b.x, b.y, z0), new Vector3(b.x, b.y, z1), new Vector3(a.x, a.y, z1), sides);
            }
            return this;
        }

        /// <summary>Cylinder along +z (smooth sides, flat caps).</summary>
        public MeshKit Cylinder(Vector3 center, float radius, float length, int segments, Shade caps, Shade sides)
        {
            float z0 = center.z - length * 0.5f, z1 = center.z + length * 0.5f;
            return Lathe(new Vector2(center.x, center.y), new[] { new Vector2(radius, z0), new Vector2(radius, z1) }, Mathf.Max(segments, 12), sides, caps, caps);
        }

        /// <summary>
        /// Surface of revolution around the z axis through <paramref name="axis"/> (x, y). The profile is a list of
        /// (radius, z) points; sides are smooth around the axis, hard between profile segments (crisp steps).
        /// The ends are closed with flat caps when their radius is not zero.
        /// </summary>
        public MeshKit Lathe(Vector2 axis, Vector2[] profile, int segments, Shade sides, Shade? startCap = null, Shade? endCap = null)
        {
            sides = FlatOf(sides); // hatching stretched around narrow curved strips reads as noise
            LatheRaw(axis, profile, segments, sides, startCap, endCap);
            return this;
        }

        private void LatheRaw(Vector2 axis, Vector2[] profile, int segments, Shade sides, Shade? startCap, Shade? endCap)
        {
            for (int s = 0; s + 1 < profile.Length; s++)
            {
                Vector2 p = profile[s], q = profile[s + 1];
                float dr = q.x - p.x, dz = q.y - p.y;
                Vector2 n2 = new Vector2(dz, -dr).normalized; // outward (radius, z) normal when z grows along the profile
                if (dz < 0f) n2 = -n2;
                for (int i = 0; i < segments; i++)
                {
                    float a0 = (float)i / segments * Mathf.PI * 2f, a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                    Vector3 d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f), d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f);
                    Vector3 c = new Vector3(axis.x, axis.y, 0f);
                    Vector3 v00 = c + d0 * p.x + Vector3.forward * p.y, v01 = c + d1 * p.x + Vector3.forward * p.y;
                    Vector3 v10 = c + d0 * q.x + Vector3.forward * q.y, v11 = c + d1 * q.x + Vector3.forward * q.y;
                    Vector3 n0 = (d0 * n2.x + Vector3.forward * n2.y).normalized, n1 = (d1 * n2.x + Vector3.forward * n2.y).normalized;
                    if (p.x < 0.00001f) TriN(v00, v11, v10, n0, n1, n0, sides, n0 + n1);
                    else if (q.x < 0.00001f) TriN(v00, v01, v10, n0, n1, n0, sides, n0 + n1);
                    else QuadN(v00, v01, v11, v10, n0, n1, n1, n0, sides, n0 + n1);
                }
            }
            if (startCap.HasValue && profile[0].x > 0.00001f) Disc(axis, profile[0].x, profile[0].y, segments, -1f, startCap.Value);
            Vector2 last = profile[profile.Length - 1];
            if (endCap.HasValue && last.x > 0.00001f) Disc(axis, last.x, last.y, segments, 1f, endCap.Value);

        }

        private void Disc(Vector2 axis, float r, float z, int segments, float facing, Shade shade)
        {
            Vector3 c = new Vector3(axis.x, axis.y, z);
            for (int i = 0; i < segments; i++)
            {
                float a0 = (float)i / segments * Mathf.PI * 2f, a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                Vector3 p0 = c + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0)) * r, p1 = c + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1)) * r;
                Vector3 n = Vector3.forward * facing;
                TriN(c, p0, p1, n, n, n, MeshKit.FlatOf(shade), n);
            }
        }

        /// <summary>Hollow tube along +z (outer and inner walls, ring faces at both ends): muzzles, bores, sleeves.</summary>
        public MeshKit Tube(Vector3 center, float outer, float inner, float length, int segments, Shade outside, Shade inside)
        {
            float z0 = center.z - length * 0.5f, z1 = center.z + length * 0.5f;
            var axis = new Vector2(center.x, center.y);
            Lathe(axis, new[] { new Vector2(outer, z0), new Vector2(outer, z1) }, segments, outside);
            for (int i = 0; i < segments; i++)
            {
                float a0 = (float)i / segments * Mathf.PI * 2f, a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1));
                Vector3 c = new Vector3(center.x, center.y, 0f);
                // inner wall faces the axis
                QuadN(c + d0 * inner + Vector3.forward * z0, c + d0 * inner + Vector3.forward * z1, c + d1 * inner + Vector3.forward * z1, c + d1 * inner + Vector3.forward * z0,
                    -d0, -d0, -d1, -d1, inside, -(d0 + d1));
                // end rings
                foreach (float z in new[] { z0, z1 })
                {
                    Vector3 n = Vector3.forward * (z > center.z ? 1f : -1f);
                    QuadN(c + d0 * inner + Vector3.forward * z, c + d0 * outer + Vector3.forward * z, c + d1 * outer + Vector3.forward * z, c + d1 * inner + Vector3.forward * z,
                        n, n, n, n, MeshKit.FlatOf(outside), n);
                }
            }
            return this;
        }

        /// <summary>
        /// Side silhouette: a simple polygon drawn in the (z, y) plane (z forward, y up — as a gun seen from its
        /// right side), extruded across x from x0 to x1. Concave outlines are fine. With a chamfer, the outline edges
        /// of both faces are bevelled, which the game's ink outlines pick up nicely.
        /// </summary>
        public MeshKit Profile(Vector2[] zy, float x0, float x1, Shade faces, Shade? walls = null, float chamfer = 0f)
        {
            Vector2[] poly = SignedArea(zy) < 0f ? zy.Reverse().ToArray() : zy; // counter-clockwise in (z, y)
            Shade w = walls ?? faces;
            float b = Mathf.Min(chamfer, (x1 - x0) * 0.45f);
            Vector2[] inner = b > 0f ? Offset(poly, -b) : poly;
            float xa = x0 + b, xb = x1 - b;
            // faces (+x and -x)
            List<int[]> tris = Triangulate(inner);
            foreach (int[] t in tris)
            {
                TriFacing(V(x1, inner[t[0]]), V(x1, inner[t[1]]), V(x1, inner[t[2]]), Vector3.right, FlatOf(faces));
                TriFacing(V(x0, inner[t[0]]), V(x0, inner[t[2]]), V(x0, inner[t[1]]), Vector3.left, FlatOf(faces));
            }
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = poly[i], q = poly[(i + 1) % n];
                Vector2 e = q - p;
                Vector3 outward = new Vector3(0f, -e.x, e.y).normalized; // (z, y) edge normal for a CCW outline
                QuadFacing(V(xa, p), V(xa, q), V(xb, q), V(xb, p), outward, w);
                if (b > 0f)
                {
                    Vector2 pi = inner[i], qi = inner[(i + 1) % n];
                    QuadFacing(V(xb, p), V(xb, q), V(x1, qi), V(x1, pi), (outward + Vector3.right).normalized, w);
                    QuadFacing(V(xa, p), V(xa, q), V(x0, qi), V(x0, pi), (outward + Vector3.left).normalized, w);
                }
            }
            return this;
        }

        /// <summary>Box with all twelve edges chamfered.</summary>
        public MeshKit ChamferBox(Vector3 center, Vector3 size, float chamfer, Shade faces, Shade? walls = null)
        {
            float b = Mathf.Min(chamfer, size.y * 0.45f, size.z * 0.45f);
            float hz = size.z * 0.5f, hy = size.y * 0.5f;
            var oct = new[]
            {
                new Vector2(-hz + b, -hy), new Vector2(hz - b, -hy), new Vector2(hz, -hy + b), new Vector2(hz, hy - b),
                new Vector2(hz - b, hy), new Vector2(-hz + b, hy), new Vector2(-hz, hy - b), new Vector2(-hz, -hy + b),
            }.Select(p => p + new Vector2(center.z, center.y)).ToArray();
            return Profile(oct, center.x - size.x * 0.5f, center.x + size.x * 0.5f, faces, walls, chamfer);
        }

        private static Vector3 V(float x, Vector2 zy) => new Vector3(x, zy.y, zy.x);

        private static float SignedArea(Vector2[] p)
        {
            float a = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 u = p[i], v = p[(i + 1) % p.Length];
                a += u.x * v.y - v.x * u.y;
            }
            return a * 0.5f;
        }

        /// <summary>Moves each vertex of a CCW outline along its corner bisector (negative = inwards).</summary>
        private static Vector2[] Offset(Vector2[] p, float d)
        {
            int n = p.Length;
            var r = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 a = p[(i + n - 1) % n], b = p[i], c = p[(i + 1) % n];
                Vector2 e0 = (b - a).normalized, e1 = (c - b).normalized;
                Vector2 n0 = new Vector2(e0.y, -e0.x), n1 = new Vector2(e1.y, -e1.x); // outward normals (CCW)
                Vector2 bis = (n0 + n1);
                float len = bis.magnitude;
                if (len < 0.0001f) { r[i] = b + n0 * d; continue; }
                bis /= len;
                float cos = Mathf.Max(0.35f, Vector2.Dot(bis, n0));
                r[i] = b + bis * (d / cos);
            }
            return r;
        }

        /// <summary>Ear clipping of a simple CCW polygon.</summary>
        private static List<int[]> Triangulate(Vector2[] p)
        {
            var result = new List<int[]>();
            var idx = Enumerable.Range(0, p.Length).ToList();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 5000)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Vector2 a = p[ia], b = p[ib], c = p[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0f) continue; // reflex corner
                    bool inside = false;
                    foreach (int j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (InTri(p[j], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    result.Add(new[] { ia, ib, ic });
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // degenerate outline: fan the rest
            }
            for (int i = 1; i + 1 < idx.Count; i++) result.Add(new[] { idx[0], idx[i], idx[i + 1] });
            return result;
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        /// <summary>Quad/triangle whose winding is fixed so the face points along <paramref name="outward"/>.</summary>
        public void QuadFacing(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Shade shade)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) Quad(d, c, b, a, shade);
            else Quad(a, b, c, d, shade);
        }

        public void TriFacing(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Shade shade)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) Tri(a, c, b, shade);
            else Tri(a, b, c, shade);
        }

        /// <summary>Quad with its own vertex normals (smooth surfaces); winding follows <paramref name="outward"/>.</summary>
        public void QuadN(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Shade shade, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                (a, d) = (d, a); (na, nd) = (nd, na);
                (b, c) = (c, b); (nb, nc) = (nc, nb);
            }
            a = Transform.MultiplyPoint3x4(a); b = Transform.MultiplyPoint3x4(b);
            c = Transform.MultiplyPoint3x4(c); d = Transform.MultiplyPoint3x4(d);
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            _n.Add(Transform.MultiplyVector(na).normalized); _n.Add(Transform.MultiplyVector(nb).normalized);
            _n.Add(Transform.MultiplyVector(nc).normalized); _n.Add(Transform.MultiplyVector(nd).normalized);
            float w = (b - a).magnitude, hgt = (d - a).magnitude;
            AddUvs(shade, new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, hgt), new Vector2(0, hgt) });
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void TriN(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Shade shade, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                (b, c) = (c, b); (nb, nc) = (nc, nb);
            }
            a = Transform.MultiplyPoint3x4(a); b = Transform.MultiplyPoint3x4(b); c = Transform.MultiplyPoint3x4(c);
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            _n.Add(Transform.MultiplyVector(na).normalized); _n.Add(Transform.MultiplyVector(nb).normalized); _n.Add(Transform.MultiplyVector(nc).normalized);
            float w = (b - a).magnitude;
            Vector3 ab = (b - a).normalized;
            float cx = Vector3.Dot(c - a, ab), cy = (c - a - ab * cx).magnitude;
            AddUvs(shade, new[] { Vector2.zero, new Vector2(w, 0), new Vector2(cx, cy) });
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        /// <summary>Points on an arc in the (z, y) plane, for rounded silhouettes (from angle a0 to a1, degrees).</summary>
        public static IEnumerable<Vector2> Arc(Vector2 center, float radius, float a0, float a1, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, (float)i / steps) * Mathf.Deg2Rad;
                yield return center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            }
        }

        /// <summary>Cone/frustum along +z from radius r0 (at z0) to r1 (at z1).</summary>
        public MeshKit Frustum(Vector3 baseCenter, float r0, float r1, float length, int segments, Shade shade)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = (float)i / segments * Mathf.PI * 2f, a1 = (float)(i + 1) / segments * Mathf.PI * 2f;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1));
                Vector3 p00 = baseCenter + d0 * r0, p01 = baseCenter + d1 * r0;
                Vector3 p10 = baseCenter + d0 * r1 + Vector3.forward * length, p11 = baseCenter + d1 * r1 + Vector3.forward * length;
                if (r1 > 0.0001f) Quad(p00, p01, p11, p10, shade);
                else Tri(p00, p01, p10, shade);
                Tri(baseCenter, p01, p00, shade);
            }
            return this;
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Shade shade)
        {
            a = Transform.MultiplyPoint3x4(a); b = Transform.MultiplyPoint3x4(b);
            c = Transform.MultiplyPoint3x4(c); d = Transform.MultiplyPoint3x4(d);
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) _n.Add(normal);
            float w = (b - a).magnitude, hgt = (d - a).magnitude;
            AddUvs(shade, new[] { new Vector2(0, 0), new Vector2(w, 0), new Vector2(w, hgt), new Vector2(0, hgt) });
            // Same convention as the game's meshes: the face points along cross(b - a, c - a).
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Shade shade)
        {
            a = Transform.MultiplyPoint3x4(a); b = Transform.MultiplyPoint3x4(b); c = Transform.MultiplyPoint3x4(c);
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            for (int k = 0; k < 3; k++) _n.Add(normal);
            float w = (b - a).magnitude;
            Vector3 ab = (b - a).normalized;
            float cx = Vector3.Dot(c - a, ab), cy = (c - a - ab * cx).magnitude;
            AddUvs(shade, new[] { Vector2.zero, new Vector2(w, 0), new Vector2(cx, cy) });
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        private void AddUvs(Shade shade, Vector2[] local)
        {
            (Rect r, int palette) = Finishes[shade];
            var pal = new Vector2((palette + 0.5f) / 32f, 0.5f / 32f);
            for (int i = 0; i < local.Length; i++) _uv2.Add(pal);
            float minX = local.Min(p => p.x), minY = local.Min(p => p.y);
            float spanX = Mathf.Max(0.0001f, local.Max(p => p.x) - minX), spanY = Mathf.Max(0.0001f, local.Max(p => p.y) - minY);
            float su = Mathf.Min(spanX * TexelsPerMetre, r.width), sv = Mathf.Min(spanY * TexelsPerMetre, r.height);
            foreach (Vector2 p in local)
                _uv.Add(new Vector2(r.xMin + (p.x - minX) / spanX * su, r.yMin + (p.y - minY) / spanY * sv));
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(_v);
            m.SetNormals(_n);
            m.SetUVs(0, _uv);
            m.SetUVs(1, _uv2);
            m.SetTriangles(_t, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            return m;
        }
    }
}
