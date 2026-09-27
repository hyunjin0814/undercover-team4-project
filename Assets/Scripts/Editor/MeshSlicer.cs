using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 메시를 축정렬 상자로 클리핑(Sutherland–Hodgman)해 독립된 메시 에셋으로 만드는 에디터 도구.
/// 통짜 건물 메시에서 벽판 구간만 오려 쓰는 용도이며, 단면은 막지 않고 서브메시 구성은 보존한다.
/// </summary>
public static class MeshSlicer
{
    private const float k_planeEpsilon = 1e-4f;

    private const float k_minTriangleArea = 1e-7f;

    private const float k_weldPrecision = 1000f;

    /// <summary>걸러내기 없이 순수하게 상자로만 자른다 — 삼각형은 전부 절단면에서 잘린다.</summary>
    public static Mesh SliceBox(Mesh source, Bounds box, Matrix4x4 postTransform, string newName) =>
        SliceBox(
            source,
            box,
            postTransform,
            newName,
            new Vector3(
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity
            )
        );

    /// <summary>잘린 뒤 삼각형이 하나도 안 남았을 때 <see cref="SliceBox"/>가 돌려주는 값은 null이다.</summary>
    public static Mesh SliceBox(
        Mesh source,
        Bounds box,
        Matrix4x4 postTransform,
        string newName,
        Vector3 discardBeyond
    )
    {
        if (source == null)
        {
            Debug.LogError("MeshSlicer: 원본 메시가 null이다");
            return null;
        }

        var src = new SourceData(source);
        Vector3 min = box.min;
        Vector3 max = box.max;

        var outVerts = new List<Vertex>();
        var weld = new Dictionary<long, int>();
        var subIndices = new List<int[]>();

        var poly = new List<Vertex>(12);
        var clipped = new List<Vertex>(12);

        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            int[] tris = source.GetTriangles(sub);
            var indices = new List<int>(tris.Length);

            for (int t = 0; t < tris.Length; t += 3)
            {
                if (ReachesTooFar(src, tris[t], tris[t + 1], tris[t + 2], min, max, discardBeyond))
                    continue;

                poly.Clear();
                poly.Add(src.Read(tris[t]));
                poly.Add(src.Read(tris[t + 1]));
                poly.Add(src.Read(tris[t + 2]));

                for (int axis = 0; axis < 3 && poly.Count >= 3; axis++)
                {
                    ClipHalfSpace(poly, clipped, axis, min[axis], true);
                    ClipHalfSpace(clipped, poly, axis, max[axis], false);
                }

                if (poly.Count < 3)
                    continue;

                for (int i = 1; i < poly.Count - 1; i++)
                    AddTriangle(
                        poly[0],
                        poly[i],
                        poly[i + 1],
                        postTransform,
                        outVerts,
                        weld,
                        indices
                    );
            }

            subIndices.Add(indices.ToArray());
        }

        int total = 0;
        for (int i = 0; i < subIndices.Count; i++)
            total += subIndices[i].Length;

        if (total == 0)
        {
            Debug.LogWarning(
                $"MeshSlicer: '{newName}' — 상자 안에 남은 삼각형이 없다 (구간을 확인할 것)"
            );
            return null;
        }

        var mesh = new Mesh { name = newName };

        if (outVerts.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        src.Write(mesh, outVerts);

        mesh.subMeshCount = subIndices.Count;
        for (int i = 0; i < subIndices.Count; i++)
            mesh.SetTriangles(subIndices[i], i);

        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>메시를.asset으로 저장한다. 같은 경로에 있으면 GUID를 유지한 채 내용만 갈아 끼운다.</summary>
    public static Mesh SaveAsAsset(Mesh mesh, string assetPath)
    {
        if (mesh == null)
            return null;

        string dir = System.IO.Path.GetDirectoryName(assetPath);
        if (!AssetDatabase.IsValidFolder(dir))
            System.IO.Directory.CreateDirectory(dir);

        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(mesh, assetPath);
            AssetDatabase.SaveAssets();
            return mesh;
        }

        CopyInto(mesh, existing);
        Object.DestroyImmediate(mesh);
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssets();
        return existing;
    }

    private static void CopyInto(Mesh from, Mesh to)
    {
        to.Clear();
        to.indexFormat = from.indexFormat;
        to.name = from.name;

        to.vertices = from.vertices;
        to.normals = from.normals;
        to.tangents = from.tangents;
        to.uv = from.uv;
        to.uv2 = from.uv2;
        to.colors = from.colors;

        to.subMeshCount = from.subMeshCount;
        for (int i = 0; i < from.subMeshCount; i++)
            to.SetTriangles(from.GetTriangles(i), i);

        to.RecalculateBounds();
    }

    private static void ClipHalfSpace(
        List<Vertex> src,
        List<Vertex> dst,
        int axis,
        float value,
        bool keepAbove
    )
    {
        dst.Clear();
        int n = src.Count;
        if (n == 0)
            return;

        for (int i = 0; i < n; i++)
        {
            Vertex cur = src[i];
            Vertex nxt = src[(i + 1) % n];

            float dc = Distance(cur, axis, value, keepAbove);
            float dn = Distance(nxt, axis, value, keepAbove);

            bool inCur = dc >= -k_planeEpsilon;
            bool inNxt = dn >= -k_planeEpsilon;

            if (inCur)
                dst.Add(cur);

            if (inCur != inNxt)
            {
                float denom = dc - dn;
                if (Mathf.Abs(denom) > k_planeEpsilon)
                    dst.Add(Vertex.Lerp(cur, nxt, dc / denom));
            }
        }
    }

    private static bool ReachesTooFar(
        SourceData src,
        int i0,
        int i1,
        int i2,
        Vector3 min,
        Vector3 max,
        Vector3 tolerance
    )
    {
        Vector3 a = src.Position(i0);
        Vector3 b = src.Position(i1);
        Vector3 c = src.Position(i2);

        for (int axis = 0; axis < 3; axis++)
        {
            if (float.IsPositiveInfinity(tolerance[axis]))
                continue;

            float lo = Mathf.Min(a[axis], Mathf.Min(b[axis], c[axis]));
            float hi = Mathf.Max(a[axis], Mathf.Max(b[axis], c[axis]));
            if (min[axis] - lo > tolerance[axis] || hi - max[axis] > tolerance[axis])
                return true;
        }

        return false;
    }

    private static float Distance(Vertex v, int axis, float value, bool keepAbove)
    {
        float p = v.Position[axis];
        return keepAbove ? p - value : value - p;
    }

    private static void AddTriangle(
        Vertex a,
        Vertex b,
        Vertex c,
        Matrix4x4 xform,
        List<Vertex> verts,
        Dictionary<long, int> weld,
        List<int> indices
    )
    {
        if (
            Vector3.Cross(b.Position - a.Position, c.Position - a.Position).sqrMagnitude
            < k_minTriangleArea
        )
            return;

        indices.Add(AddVertex(a.Transformed(xform), verts, weld));
        indices.Add(AddVertex(b.Transformed(xform), verts, weld));
        indices.Add(AddVertex(c.Transformed(xform), verts, weld));
    }

    private static int AddVertex(Vertex v, List<Vertex> verts, Dictionary<long, int> weld)
    {
        long key = v.WeldKey();
        if (weld.TryGetValue(key, out int existing))
            return existing;

        verts.Add(v);
        weld[key] = verts.Count - 1;
        return verts.Count - 1;
    }

    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Uv0;
        public Vector2 Uv1;
        public Color Color;

        public static Vertex Lerp(Vertex a, Vertex b, float t)
        {
            return new Vertex
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = Vector3.Slerp(a.Normal, b.Normal, t),
                Tangent = Vector4.Lerp(a.Tangent, b.Tangent, t),
                Uv0 = Vector2.Lerp(a.Uv0, b.Uv0, t),
                Uv1 = Vector2.Lerp(a.Uv1, b.Uv1, t),
                Color = Color.Lerp(a.Color, b.Color, t),
            };
        }

        public Vertex Transformed(Matrix4x4 m)
        {
            Vector3 tan3 = m.MultiplyVector(new Vector3(Tangent.x, Tangent.y, Tangent.z));
            return new Vertex
            {
                Position = m.MultiplyPoint3x4(Position),
                Normal = m.MultiplyVector(Normal).normalized,
                Tangent = new Vector4(tan3.x, tan3.y, tan3.z, Tangent.w),
                Uv0 = Uv0,
                Uv1 = Uv1,
                Color = Color,
            };
        }

        public long WeldKey()
        {
            long x = (long)Mathf.Round(Position.x * k_weldPrecision);
            long y = (long)Mathf.Round(Position.y * k_weldPrecision);
            long z = (long)Mathf.Round(Position.z * k_weldPrecision);
            long u = (long)Mathf.Round(Uv0.x * k_weldPrecision);
            long v = (long)Mathf.Round(Uv0.y * k_weldPrecision);
            long key = x;
            key = key * 1000003L + y;
            key = key * 1000003L + z;
            key = key * 1000003L + u;
            key = key * 1000003L + v;
            return key;
        }
    }

    private sealed class SourceData
    {
        private readonly Vector3[] m_positions;
        private readonly Vector3[] m_normals;
        private readonly Vector4[] m_tangents;
        private readonly Vector2[] m_uv0;
        private readonly Vector2[] m_uv1;
        private readonly Color[] m_colors;

        public SourceData(Mesh mesh)
        {
            m_positions = mesh.vertices;
            m_normals = mesh.normals;
            m_tangents = mesh.tangents;
            m_uv0 = mesh.uv;
            m_uv1 = mesh.uv2;
            m_colors = mesh.colors;
        }

        private static bool Has<T>(T[] a, int count) => a != null && a.Length == count;

        /// <summary>위치만 읽는다 — 버릴지 말지 판단할 때 나머지 스트림까지 만들 이유가 없다.</summary>
        public Vector3 Position(int i) => m_positions[i];

        public Vertex Read(int i)
        {
            int n = m_positions.Length;
            return new Vertex
            {
                Position = m_positions[i],
                Normal = Has(m_normals, n) ? m_normals[i] : Vector3.up,
                Tangent = Has(m_tangents, n) ? m_tangents[i] : new Vector4(1f, 0f, 0f, 1f),
                Uv0 = Has(m_uv0, n) ? m_uv0[i] : Vector2.zero,
                Uv1 = Has(m_uv1, n) ? m_uv1[i] : Vector2.zero,
                Color = Has(m_colors, n) ? m_colors[i] : Color.white,
            };
        }

        public void Write(Mesh mesh, List<Vertex> verts)
        {
            int n = m_positions.Length;
            int count = verts.Count;

            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
                positions[i] = verts[i].Position;
            mesh.vertices = positions;

            if (Has(m_normals, n))
            {
                var a = new Vector3[count];
                for (int i = 0; i < count; i++)
                    a[i] = verts[i].Normal;
                mesh.normals = a;
            }

            if (Has(m_tangents, n))
            {
                var a = new Vector4[count];
                for (int i = 0; i < count; i++)
                    a[i] = verts[i].Tangent;
                mesh.tangents = a;
            }

            if (Has(m_uv0, n))
            {
                var a = new Vector2[count];
                for (int i = 0; i < count; i++)
                    a[i] = verts[i].Uv0;
                mesh.uv = a;
            }

            if (Has(m_uv1, n))
            {
                var a = new Vector2[count];
                for (int i = 0; i < count; i++)
                    a[i] = verts[i].Uv1;
                mesh.uv2 = a;
            }

            if (Has(m_colors, n))
            {
                var a = new Color[count];
                for (int i = 0; i < count; i++)
                    a[i] = verts[i].Color;
                mesh.colors = a;
            }
        }
    }
}
