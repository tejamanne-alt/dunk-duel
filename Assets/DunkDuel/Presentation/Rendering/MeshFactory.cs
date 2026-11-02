using System.Collections.Generic;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Procedural graybox meshes (no imported assets, no colliders). Each call builds a new mesh; callers
    /// cache shared ones. Meshes are centered on their pivot unless noted.
    /// </summary>
    public static class MeshFactory
    {
        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
            public Color Tint = Color.white;

            public int Add(Vector3 v, Vector3 n, Vector2 uv)
            {
                V.Add(v);
                N.Add(n);
                UV.Add(uv);
                C.Add(Tint);
                return V.Count - 1;
            }

            public void Tri(int a, int b, int c)
            {
                T.Add(a);
                T.Add(b);
                T.Add(c);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                int i0 = Add(a, n, new Vector2(0, 0));
                int i1 = Add(b, n, new Vector2(1, 0));
                int i2 = Add(c, n, new Vector2(1, 1));
                int i3 = Add(d, n, new Vector2(0, 1));
                Tri(i0, i2, i1);
                Tri(i0, i3, i2);
            }

            public Mesh Build(string name, bool recalcNormals = false)
            {
                var m = new Mesh { name = name };
                m.vertices = V.ToArray();
                m.normals = N.ToArray();
                m.uv = UV.ToArray();
                m.colors = C.ToArray();
                m.triangles = T.ToArray();
                if (recalcNormals) m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        public static Mesh Box(Vector3 size) => Box(size, Vector3.zero);

        /// <summary>Axis-aligned box with per-face normals; <paramref name="center"/> offsets it from the pivot.</summary>
        public static Mesh Box(Vector3 size, Vector3 center)
        {
            var b = new Builder();
            AddBox(b, size, center);
            return b.Build("Box");
        }

        private static void AddBox(Builder b, Vector3 size, Vector3 c)
        {
            Vector3 h = size * 0.5f;
            Vector3 p000 = c + new Vector3(-h.x, -h.y, -h.z), p100 = c + new Vector3(h.x, -h.y, -h.z);
            Vector3 p010 = c + new Vector3(-h.x, h.y, -h.z), p110 = c + new Vector3(h.x, h.y, -h.z);
            Vector3 p001 = c + new Vector3(-h.x, -h.y, h.z), p101 = c + new Vector3(h.x, -h.y, h.z);
            Vector3 p011 = c + new Vector3(-h.x, h.y, h.z), p111 = c + new Vector3(h.x, h.y, h.z);
            b.Quad(p000, p100, p110, p010, Vector3.back);    // -Z
            b.Quad(p101, p001, p011, p111, Vector3.forward); // +Z
            b.Quad(p001, p000, p010, p011, Vector3.left);    // -X
            b.Quad(p100, p101, p111, p110, Vector3.right);   // +X
            b.Quad(p010, p110, p111, p011, Vector3.up);      // +Y
            b.Quad(p001, p101, p100, p000, Vector3.down);    // -Y
        }

        /// <summary>Vertical cylinder from y = 0 to y = height (pivot at the base center).</summary>
        public static Mesh Cylinder(float radius, float height, int segments = 12, bool caps = true, float topRadius = -1f)
        {
            if (topRadius < 0f) topRadius = radius;
            var b = new Builder();
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                b.Add(dir * radius, dir, new Vector2(i / (float)segments, 0f));
                b.Add(dir * topRadius + Vector3.up * height, dir, new Vector2(i / (float)segments, 1f));
            }
            for (int i = 0; i < segments; i++)
            {
                int k = i * 2;
                b.Tri(k, k + 1, k + 3);
                b.Tri(k, k + 3, k + 2);
            }
            if (caps)
            {
                int top = b.Add(Vector3.up * height, Vector3.up, new Vector2(0.5f, 0.5f));
                int bottom = b.Add(Vector3.zero, Vector3.down, new Vector2(0.5f, 0.5f));
                int ringTop = b.V.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float a = i / (float)segments * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    b.Add(dir * topRadius + Vector3.up * height, Vector3.up, new Vector2(0.5f + dir.x * 0.5f, 0.5f + dir.z * 0.5f));
                }
                int ringBottom = b.V.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float a = i / (float)segments * Mathf.PI * 2f;
                    var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    b.Add(dir * radius, Vector3.down, new Vector2(0.5f + dir.x * 0.5f, 0.5f + dir.z * 0.5f));
                }
                for (int i = 0; i < segments; i++)
                {
                    b.Tri(top, ringTop + i + 1, ringTop + i);
                    b.Tri(bottom, ringBottom + i, ringBottom + i + 1);
                }
            }
            return b.Build("Cylinder");
        }

        /// <summary>UV sphere centered on the pivot.</summary>
        public static Mesh Sphere(float radius, int segments = 14, int rings = 9)
        {
            var b = new Builder();
            for (int r = 0; r <= rings; r++)
            {
                float v = r / (float)rings;
                float phi = v * Mathf.PI;
                for (int s = 0; s <= segments; s++)
                {
                    float u = s / (float)segments;
                    float theta = u * Mathf.PI * 2f;
                    var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                    b.Add(n * radius, n, new Vector2(u, 1f - v));
                }
            }
            int stride = segments + 1;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * stride + s;
                    int c = a + stride;
                    b.Tri(a, a + 1, c + 1);
                    b.Tri(a, c + 1, c);
                }
            }
            return b.Build("Sphere");
        }

        /// <summary>Capsule along Y from y = 0 to y = length (pivot at the bottom), for limbs hanging from a joint use negative offsets.</summary>
        public static Mesh Capsule(float radius, float length, int segments = 10, int capRings = 4)
        {
            var b = new Builder();
            float body = Mathf.Max(0f, length - 2f * radius);
            int stride = segments + 1;
            int rowsTotal = 0;
            // Bottom hemisphere, cylinder rings, top hemisphere as one lathe profile.
            var profile = new List<Vector2>(); // (radius, y)
            var normals = new List<Vector2>();
            for (int i = 0; i <= capRings; i++)
            {
                float a = -Mathf.PI * 0.5f + i / (float)capRings * Mathf.PI * 0.5f;
                profile.Add(new Vector2(Mathf.Cos(a) * radius, radius + Mathf.Sin(a) * radius));
                normals.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)));
            }
            for (int i = 0; i <= capRings; i++)
            {
                float a = i / (float)capRings * Mathf.PI * 0.5f;
                profile.Add(new Vector2(Mathf.Cos(a) * radius, radius + body + Mathf.Sin(a) * radius));
                normals.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)));
            }
            for (int r = 0; r < profile.Count; r++)
            {
                for (int s = 0; s <= segments; s++)
                {
                    float theta = s / (float)segments * Mathf.PI * 2f;
                    float cx = Mathf.Cos(theta), cz = Mathf.Sin(theta);
                    var n = new Vector3(cx * normals[r].x, normals[r].y, cz * normals[r].x);
                    b.Add(new Vector3(cx * profile[r].x, profile[r].y, cz * profile[r].x), n.normalized, new Vector2(s / (float)segments, profile[r].y / Mathf.Max(0.001f, length)));
                }
                rowsTotal++;
            }
            for (int r = 0; r < rowsTotal - 1; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * stride + s;
                    int c = a + stride;
                    b.Tri(a, c + 1, a + 1);
                    b.Tri(a, c, c + 1);
                }
            }
            return b.Build("Capsule");
        }

        /// <summary>Horizontal torus (ring in the XZ plane) centered on the pivot.</summary>
        public static Mesh Torus(float major, float minor, int segments = 24, int sides = 8)
        {
            var b = new Builder();
            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments * Mathf.PI * 2f;
                var center = new Vector3(Mathf.Cos(u) * major, 0f, Mathf.Sin(u) * major);
                var outward = new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));
                for (int j = 0; j <= sides; j++)
                {
                    float v = j / (float)sides * Mathf.PI * 2f;
                    Vector3 n = outward * Mathf.Cos(v) + Vector3.up * Mathf.Sin(v);
                    b.Add(center + n * minor, n, new Vector2(i / (float)segments, j / (float)sides));
                }
            }
            int stride = sides + 1;
            for (int i = 0; i < segments; i++)
            {
                for (int j = 0; j < sides; j++)
                {
                    int a = i * stride + j;
                    int c = a + stride;
                    b.Tri(a, a + 1, c + 1);
                    b.Tri(a, c + 1, c);
                }
            }
            return b.Build("Torus");
        }

        /// <summary>Quad facing -Z (toward the side camera), centered on the pivot.</summary>
        public static Mesh Quad(float width, float height)
        {
            var b = new Builder();
            float w = width * 0.5f, h = height * 0.5f;
            b.Quad(new Vector3(-w, -h, 0), new Vector3(w, -h, 0), new Vector3(w, h, 0), new Vector3(-w, h, 0), Vector3.back);
            return b.Build("Quad");
        }

        /// <summary>Quad facing -Z with a vertical color gradient (painted backdrop card).</summary>
        public static Mesh GradientQuad(float width, float height, Color bottom, Color top)
        {
            var b = new Builder();
            float w = width * 0.5f, h = height * 0.5f;
            b.Tint = bottom;
            int i0 = b.Add(new Vector3(-w, -h, 0), Vector3.back, new Vector2(0, 0));
            int i1 = b.Add(new Vector3(w, -h, 0), Vector3.back, new Vector2(1, 0));
            b.Tint = top;
            int i2 = b.Add(new Vector3(w, h, 0), Vector3.back, new Vector2(1, 1));
            int i3 = b.Add(new Vector3(-w, h, 0), Vector3.back, new Vector2(0, 1));
            b.Tri(i0, i2, i1);
            b.Tri(i0, i3, i2);
            return b.Build("GradientQuad");
        }

        /// <summary>Horizontal rectangle on the floor plane (XZ), slightly above y = 0 to avoid z-fighting.</summary>
        public static Mesh FloorRect(float x0, float x1, float z0, float z1, float y = 0f)
        {
            var b = new Builder();
            b.Quad(new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), Vector3.up);
            return b.Build("FloorRect");
        }

        /// <summary>A flat line strip on the floor through <paramref name="points"/> (XZ), <paramref name="width"/> wide.</summary>
        public static Mesh FloorStrip(IList<Vector2> points, float width, float y, bool closed = false)
        {
            var b = new Builder();
            int n = points.Count;
            if (n < 2) return b.Build("FloorStrip");
            int count = closed ? n + 1 : n;
            for (int i = 0; i < count; i++)
            {
                Vector2 p = points[i % n];
                Vector2 prev = points[closed ? (i - 1 + n) % n : Mathf.Max(0, i - 1)];
                Vector2 next = points[closed ? (i + 1) % n : Mathf.Min(n - 1, i + 1)];
                Vector2 dir = (next - prev).normalized;
                var side = new Vector2(-dir.y, dir.x) * (width * 0.5f);
                b.Add(new Vector3(p.x + side.x, y, p.y + side.y), Vector3.up, new Vector2(0, i));
                b.Add(new Vector3(p.x - side.x, y, p.y - side.y), Vector3.up, new Vector2(1, i));
            }
            for (int i = 0; i < count - 1; i++)
            {
                int k = i * 2;
                b.Tri(k, k + 2, k + 1);
                b.Tri(k + 1, k + 2, k + 3);
            }
            return b.Build("FloorStrip");
        }

        /// <summary>Points on a circle arc in the XZ plane.</summary>
        public static List<Vector2> Arc(Vector2 center, float radius, float startDeg, float endDeg, int steps)
        {
            var pts = new List<Vector2>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(startDeg, endDeg, i / (float)steps) * Mathf.Deg2Rad;
                pts.Add(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
            return pts;
        }

        /// <summary>Filled disc on the floor (XZ).</summary>
        public static Mesh FloorDisc(float radius, int segments, float y)
        {
            var b = new Builder();
            int c = b.Add(new Vector3(0, y, 0), Vector3.up, new Vector2(0.5f, 0.5f));
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                b.Add(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius), Vector3.up, new Vector2(0.5f + Mathf.Cos(a) * 0.5f, 0.5f + Mathf.Sin(a) * 0.5f));
            }
            for (int i = 0; i < segments; i++) b.Tri(c, c + i + 2, c + i + 1);
            return b.Build("FloorDisc");
        }

        /// <summary>Flat ring (annulus) in the XZ plane; used for ripples and shockwaves.</summary>
        public static Mesh Ring(float inner, float outer, int segments = 32)
        {
            var b = new Builder();
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                b.Add(d * inner, Vector3.up, new Vector2(i / (float)segments, 0));
                b.Add(d * outer, Vector3.up, new Vector2(i / (float)segments, 1));
            }
            for (int i = 0; i < segments; i++)
            {
                int k = i * 2;
                b.Tri(k, k + 3, k + 1);
                b.Tri(k, k + 2, k + 3);
            }
            return b.Build("Ring");
        }

        /// <summary>Open tapered tube hanging down from y = 0 (the net), with vertex colors for a diamond pattern feel.</summary>
        public static Mesh Net(float topRadius, float bottomRadius, float length, int segments = 16, int rows = 5)
        {
            var b = new Builder();
            for (int r = 0; r <= rows; r++)
            {
                float t = r / (float)rows;
                float rad = Mathf.Lerp(topRadius, bottomRadius, t);
                float y = -length * t;
                for (int s = 0; s <= segments; s++)
                {
                    float a = (s + (r % 2) * 0.5f) / segments * Mathf.PI * 2f;
                    var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    b.Tint = ((s + r) % 2 == 0) ? Color.white : new Color(0.85f, 0.85f, 0.85f, 1f);
                    b.Add(d * rad + Vector3.up * y, d, new Vector2(s / (float)segments, t));
                }
            }
            int stride = segments + 1;
            for (int r = 0; r < rows; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * stride + s;
                    int c = a + stride;
                    // Double-sided so the net reads from inside and out.
                    b.Tri(a, c, a + 1);
                    b.Tri(a + 1, c, c + 1);
                    b.Tri(a, a + 1, c);
                    b.Tri(a + 1, c + 1, c);
                }
            }
            return b.Build("Net");
        }

        /// <summary>One low-poly spectator (body, shoulders, head) merged into one mesh, standing on y = 0, ~1.7 m tall.</summary>
        public static Mesh Spectator()
        {
            var b = new Builder();
            AddBox(b, new Vector3(0.42f, 0.75f, 0.28f), new Vector3(0f, 0.85f, 0f));   // torso
            AddBox(b, new Vector3(0.30f, 0.48f, 0.24f), new Vector3(0f, 0.24f, 0f));   // legs
            AddBox(b, new Vector3(0.24f, 0.26f, 0.24f), new Vector3(0f, 1.42f, 0f));   // head
            AddBox(b, new Vector3(0.12f, 0.55f, 0.12f), new Vector3(-0.28f, 1.0f, 0f)); // arms
            AddBox(b, new Vector3(0.12f, 0.55f, 0.12f), new Vector3(0.28f, 1.0f, 0f));
            return b.Build("Spectator");
        }

        /// <summary>A stepped bleacher block: rows rising away from the court (+Z), each row 0.45 m high and 0.8 m deep.</summary>
        public static Mesh Bleachers(float width, int rows, float rowDepth = 0.8f, float rowHeight = 0.45f)
        {
            var b = new Builder();
            for (int r = 0; r < rows; r++)
            {
                float h = (r + 1) * rowHeight;
                AddBox(b, new Vector3(width, h, rowDepth), new Vector3(0f, h * 0.5f, r * rowDepth + rowDepth * 0.5f));
            }
            return b.Build("Bleachers");
        }
    }
}
