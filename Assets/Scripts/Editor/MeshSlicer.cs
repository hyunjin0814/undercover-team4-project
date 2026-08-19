using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 메시를 <b>축정렬 상자로 잘라</b> 새 메시 에셋을 만드는 에디터 도구. (#722)
///
/// 쓰는 이유: Synty 건물 중에는 <b>통짜 메시 하나로 된 것</b>이 있어(예: <c>SM_Bld_Bank_01</c> —
/// 정점 9359 / 삼각형 6314가 단일 메시) 벽 한 조각만 떼어 모듈 벽으로 쓸 수가 없다. 이 도구는
/// 그런 메시에서 원하는 구간만 오려 <b>독립된 벽판 메시</b>로 저장한다.
///
/// <b>진짜 클리핑을 한다</b> — 상자 안에 완전히 든 삼각형만 고르는 방식이 아니다. 절단면을 가로지르는
/// 삼각형은 Sutherland–Hodgman으로 잘라 새 정점을 만들고, 그 정점의 UV·노멀·탱젠트·색을 원본에서
/// 보간해 채운다. 통째 선택 방식으로는 절단면이 5m 같은 <b>정점선에 딱 맞는 곳에서만</b> 쓸 수 있는데,
/// 우리 본부 벽 격자는 2.5m라 그 사이를 지나는 삼각형이 반드시 생긴다.
///
/// <b>단면은 막지 않는다</b> — 잘린 자리는 열린 채로 남는다. 벽판끼리 맞대 세우면 보이지 않고,
/// 원본이 양면 벽(앞뒤가 각각 그려진 판)이면 두께 자체가 없어 막을 단면도 없다. 구간 끝이 노출되는
/// 자리는 코너 조각을 따로 오려 쓰거나 프롭으로 가릴 것.
///
/// <b>서브메시는 보존한다</b> — 머티리얼이 여럿인 원본도 인덱스 구성이 유지된다. 잘린 뒤 삼각형이
/// 하나도 남지 않은 서브메시는 빈 채로 남으므로 머티리얼 슬롯 순서가 어긋나지 않는다.
///
/// 사용 예 (에디터 코드에서 호출):
/// <code>
/// Mesh src = AssetDatabase.LoadAssetAtPath&lt;GameObject&gt;(fbx).GetComponentInChildren&lt;MeshFilter&gt;().sharedMesh;
/// Bounds slab = new Bounds(); slab.SetMinMax(new Vector3(-0.6f, 0f, -5f), new Vector3(0.6f, 3.2f, 0f));
/// Mesh panel = MeshSlicer.SliceBox(src, slab, Matrix4x4.Rotate(Quaternion.Euler(0f, 90f, 0f)), "SM_HQ_Wall_5m");
/// MeshSlicer.SaveAsAsset(panel, "Assets/Prefabs/HQ/Meshes/SM_HQ_Wall_5m.asset");
/// </code>
/// </summary>
public static class MeshSlicer
{
    // 절단면 판정 여유 — 정점이 절단면에 정확히 얹혔을 때 0면적 조각이 생기는 것을 막는다.
    // Synty 메시 좌표는 소수 두 자리 수준이라 그보다 두 자리 아래로 잡았다.
    private const float k_planeEpsilon = 1e-4f;

    // 이보다 작은 면적의 삼각형은 버린다 — 절단면을 스치듯 지난 삼각형이 남기는 바늘 조각이다.
    private const float k_minTriangleArea = 1e-7f;

    // 정점 합치기 격자 — 이 간격 안에서 위치·UV가 같으면 같은 정점으로 본다 (1mm).
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
    /// <param name="source">원본 메시. <c>isReadable</c>이 false여도 에디터에서는 읽힌다.</param>
    /// <param name="box">잘라낼 구간 — 원본 <b>로컬 좌표계</b>의 축정렬 상자.</param>
    /// <param name="postTransform">
    /// 잘라낸 뒤 정점에 먹일 변환 — 벽판을 배치 규약(메시가 로컬 x −N..0, y 0..높이, z≈0)에 맞추는 데 쓴다.
    /// 회전을 넣으면 노멀·탱젠트도 함께 돈다. 항등이면 원본 좌표 그대로.
    /// </param>
    /// <param name="discardBeyond">
    /// <b>박스 밖으로 이만큼(m)보다 더 벗어난 삼각형은 자르지 않고 통째로 버린다 — 축별로 따로 준다.</b>
    /// 걸러낼 축에만 유한한 값을 주고 나머지는 무한대로 둔다. 기본값(전 축 무한대)은 "전부 자른다".
    ///
    /// 건물 통짜 메시에서 벽만 떼어낼 때 필요하다. <c>SM_Bld_Bank_01</c>의 <b>바닥·천장 슬래브는 벽에서
    /// 건물 안쪽으로 10m를 뻗는</b>(그런 삼각형이 1678개, 면적 256㎡ 중 189㎡가 바닥·천장) 한 장짜리
    /// 면이라, 절단면을 어디에 두든 잘리면서 벽판 위아래에 <b>검은 띠</b>를 남긴다. 그렇다고 얕게 자르면
    /// 이음매를 가리던 <b>기둥</b>까지 날아가 맨 이음매가 드러난다. 깊이 축에만 이 값을 두면
    /// "벽에 붙은 장식(기둥·차양)은 살리고, 실내로 길게 뻗는 슬래브는 버린다"가 한 줄로 갈린다.
    ///
    /// ⚠ <b>벽을 따라가는 축에는 절대 유한값을 주지 말 것.</b> 벽면은 모듈(5m) 전체를 덮는 큰 삼각형으로
    /// 되어 있어서, 그 축에 제한을 걸면 <b>반 칸(2.5m)짜리를 뽑을 때 벽면이 통째로 버려져 구멍이 뚫린다</b>.
    /// </param>
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

        // 폴리곤 버퍼 두 개를 번갈아 쓴다 — 6면을 차례로 자르는 동안 매번 새 리스트를 만들지 않는다.
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

                // 부채꼴 삼각분할 — Sutherland–Hodgman의 결과는 항상 볼록이라 이걸로 충분하다.
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

        // 정점 6만 개를 넘기면 16비트 인덱스로는 못 담는다 — 원본이 클 수 있으므로 미리 올려 둔다.
        if (outVerts.Count > 65000)
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        src.Write(mesh, outVerts);

        mesh.subMeshCount = subIndices.Count;
        for (int i = 0; i < subIndices.Count; i++)
            mesh.SetTriangles(subIndices[i], i);

        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// 메시를 <c>.asset</c>으로 저장한다.
    ///
    /// <b>같은 경로에 이미 있으면 지우지 않고 그 에셋의 내용을 갈아 끼운다</b> — 지웠다 새로 만들면
    /// GUID가 바뀌어 <b>이 메시를 물고 있던 프리팹·콜라이더의 배선이 전부 끊긴다</b>. 벽판은 자르는
    /// 기준을 몇 번씩 고쳐 가며 다시 뽑게 되므로, 덮어쓰기가 곧 기본 동작이어야 한다.
    /// </summary>
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
        Object.DestroyImmediate(mesh); // 내용을 옮겼으니 임시 메시는 버린다
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssets();
        return existing;
    }

    // 메시 내용만 통째로 옮긴다 — 받는 쪽 에셋의 GUID는 그대로 남는다.
    // 인덱스는 정점보다 <b>먼저 비운다</b>: 새 정점 수가 옛 인덱스 범위보다 작으면 Unity가 곧바로
    // "index out of bounds"를 던진다.
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

    // ---- 클리핑 ----

    // 반공간 하나로 폴리곤을 자른다. keepAbove면 value 이상, 아니면 value 이하를 남긴다.
    // 결과는 dst에 담는다 (src와 dst는 서로 다른 리스트여야 한다).
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

            // 변이 절단면을 가로지른다 — 교점을 만들어 끼운다. 분모가 0에 가까우면 두 점이 사실상
            // 같은 면에 있는 것이라 새 점을 만들지 않는다(0길이 변이 생기는 것을 막는다).
            if (inCur != inNxt)
            {
                float denom = dc - dn;
                if (Mathf.Abs(denom) > k_planeEpsilon)
                    dst.Add(Vertex.Lerp(cur, nxt, dc / denom));
            }
        }
    }

    // 삼각형이 상자 밖으로 tolerance보다 멀리 뻗는가 — 그렇다면 잘라 쓰는 대신 통째로 버린다.
    // 정점 하나만 멀리 나가도 버린다: 그 삼각형은 '벽에 붙은 장식'이 아니라 '벽을 지나가는 큰 면'이다.
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
        // 바늘 조각 버리기 — 절단면을 스치고 지난 삼각형이 남긴 0면적 파편이다.
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

    // ---- 정점 ----

    // 보간해야 할 속성을 한 덩어리로 묶는다. 원본에 없는 스트림은 SourceData가 기본값으로 채우고,
    // 쓸 때 다시 빼므로 여기서는 항상 있는 것처럼 다뤄도 된다.
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
                // 노멀은 정규화해 섞는다 — 선형 보간만 하면 절단면 정점의 음영이 살짝 어두워진다.
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
                Tangent = new Vector4(tan3.x, tan3.y, tan3.z, Tangent.w), // w는 종법선 방향 부호라 안 돈다
                Uv0 = Uv0,
                Uv1 = Uv1,
                Color = Color,
            };
        }

        // 위치와 UV가 1mm/1e-3 안에서 같으면 같은 정점으로 본다 — 인접 삼각형이 공유하던 정점을
        // 다시 붙여 준다. 노멀까지 키에 넣지는 않는다(하드 엣지가 뭉개질 수 있지만, 벽판 규모에서
        // 관측되지 않았고 키가 길어지는 값이 더 크다).
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

    // 원본이 어떤 스트림을 들고 있는지 한 번만 조사해 두고, 읽기·쓰기 양쪽에서 같은 판단을 쓴다 —
    // 없는 스트림을 되살려 저장하면 원본에 없던 데이터가 붙어 용량만 는다.
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
