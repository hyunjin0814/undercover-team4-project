using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 구워진 NavMesh를 격자로 훑어 도달 불가 구역과 스폰 앵커 도달성을 로그로 출력한다.
/// 메뉴: Tools/맵 NavMesh 점검.
/// </summary>
public class MapNavMeshAudit : EditorWindow
{
    private const float k_originSampleRadius = 5f;
    private const int k_maxClustersLogged = 30;
    private const float k_anchorFloatTolerance = 1.5f;

    [SerializeField]
    private Transform m_origin;

    [SerializeField]
    private float m_cellSize = 2.5f;

    [SerializeField]
    private float m_sampleRadius = 1f;

    [SerializeField]
    private string m_levels = "0, 2.6, 5.2";

    [SerializeField]
    private string m_anchorPrefixes = "NpcSpawn_, BombSpawnPoint_, BombCrate_, PlayerSpawnPoint, InmatePoint";

    [SerializeField]
    private string m_probes = "";

    [SerializeField]
    private float m_minClusterArea = 4f;

    private Vector2 m_scroll;

    [MenuItem("Tools/맵 NavMesh 점검")]
    private static void Open()
    {
        GetWindow<MapNavMeshAudit>("NavMesh 점검").minSize = new Vector2(420f, 400f);
    }

    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        m_origin = (Transform)
            EditorGUILayout.ObjectField(
                new GUIContent("기준점", "비우면 PlayerSpawnPoint를 찾아 쓴다"),
                m_origin,
                typeof(Transform),
                true
            );
        m_cellSize = EditorGUILayout.FloatField("격자 간격(m)", m_cellSize);
        m_sampleRadius = EditorGUILayout.FloatField("표본 반경(m)", m_sampleRadius);
        m_levels = EditorGUILayout.TextField(
            new GUIContent("검사 높이(m)", "다층은 층마다 따로 훑는다"),
            m_levels
        );
        m_minClusterArea = EditorGUILayout.FloatField("보고 하한(m²)", m_minClusterArea);

        EditorGUILayout.Space();
        m_anchorPrefixes = EditorGUILayout.TextField("앵커 접두사", m_anchorPrefixes);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(
            new GUIContent("지점 검사", "한 줄에 'x,y,z' 또는 'x,y,z,불가'")
        );
        m_probes = EditorGUILayout.TextArea(m_probes, GUILayout.MinHeight(60f));

        EditorGUILayout.Space();
        if (GUILayout.Button("전체 점검", GUILayout.Height(30f)))
        {
            RunAll();
        }

        if (GUILayout.Button("서피스 설정만 보기"))
        {
            LogSurfaces();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RunAll()
    {
        LogSurfaces();

        if (!TryResolveOrigin(out Vector3 origin))
        {
            return;
        }

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0)
        {
            Debug.LogError("맵 NavMesh 점검: 구워진 NavMesh가 없다");
            return;
        }

        Bounds bounds = new Bounds(tri.vertices[0], Vector3.zero);
        for (int i = 1; i < tri.vertices.Length; i++)
        {
            bounds.Encapsulate(tri.vertices[i]);
        }

        Debug.Log(
            $"맵 NavMesh 점검: 기준점 {Fmt(origin)} · 범위 x[{bounds.min.x:F1}~{bounds.max.x:F1}] "
                + $"z[{bounds.min.z:F1}~{bounds.max.z:F1}] y[{bounds.min.y:F1}~{bounds.max.y:F1}]"
        );

        var clusters = new List<Cluster>();
        var reachable = new List<Vector3>();

        foreach (float level in ParseFloats(m_levels))
        {
            AuditLevel(origin, bounds, level, clusters, reachable);
        }

        AnalyzeClusters(clusters, reachable);
        AuditAnchors(origin);
        AuditProbes(origin);
    }

    private class Cluster
    {
        public float Level;
        public float Area;
        public Vector3 Center;
        public Bounds Box;
        public Vector3 Sample;
        public List<Vector3> Points;
        public int Group = -1;
    }

    private void AuditLevel(
        Vector3 origin,
        Bounds bounds,
        float level,
        List<Cluster> clusters,
        List<Vector3> reachable
    )
    {
        int cols = Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / m_cellSize));
        int rows = Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / m_cellSize));

        var state = new int[cols * rows];
        var hitPos = new Vector3[cols * rows];
        var path = new NavMeshPath();

        int sampled = 0;
        int reached = 0;

        try
        {
            for (int r = 0; r < rows; r++)
            {
                if (
                    EditorUtility.DisplayCancelableProgressBar(
                        "맵 NavMesh 점검",
                        $"y={level:F2} ({r + 1}/{rows})",
                        (float)r / rows
                    )
                )
                {
                    Debug.LogWarning($"맵 NavMesh 점검: y={level:F2} 검사 취소");
                    return;
                }

                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    state[i] = -1;

                    var probe = new Vector3(
                        bounds.min.x + (c + 0.5f) * m_cellSize,
                        level,
                        bounds.min.z + (r + 0.5f) * m_cellSize
                    );

                    if (
                        !NavMesh.SamplePosition(probe, out NavMeshHit hit, m_sampleRadius, NavMesh.AllAreas)
                        || Mathf.Abs(hit.position.y - level) > m_sampleRadius
                    )
                    {
                        continue;
                    }

                    sampled++;
                    hitPos[i] = hit.position;

                    bool ok =
                        NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                        && path.status == NavMeshPathStatus.PathComplete;

                    state[i] = ok ? 1 : 0;
                    if (ok)
                    {
                        reached++;
                        reachable.Add(hit.position);
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (sampled == 0)
        {
            Debug.Log($"맵 NavMesh 점검: y={level:F2} — NavMesh 없음");
            return;
        }

        string head =
            $"맵 NavMesh 점검: y={level:F2} — 도달률 {100f * reached / sampled:F1}% ({reached}/{sampled}칸)";

        int before = clusters.Count;
        CollectClusters(state, hitPos, cols, rows, level, clusters);
        int found = clusters.Count - before;

        if (found == 0)
        {
            Debug.Log($"{head} · 고립 구역 없음");
        }
        else
        {
            Debug.Log($"{head} · 고립 덩어리 {found}개 (자세한 것은 아래 고립 덩어리 분석)");
        }
    }

    private void CollectClusters(
        int[] state,
        Vector3[] hitPos,
        int cols,
        int rows,
        float level,
        List<Cluster> clusters
    )
    {
        float cellArea = m_cellSize * m_cellSize;
        var seen = new bool[state.Length];
        var stack = new Stack<int>();

        for (int start = 0; start < state.Length; start++)
        {
            if (state[start] != 0 || seen[start])
            {
                continue;
            }

            seen[start] = true;
            stack.Push(start);

            int count = 0;
            Vector3 sum = Vector3.zero;
            var box = new Bounds(hitPos[start], Vector3.zero);
            var points = new List<Vector3>();

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                count++;
                sum += hitPos[i];
                box.Encapsulate(hitPos[i]);
                points.Add(hitPos[i]);

                int c = i % cols;
                int r = i / cols;
                TryPush(state, seen, stack, cols, rows, c - 1, r);
                TryPush(state, seen, stack, cols, rows, c + 1, r);
                TryPush(state, seen, stack, cols, rows, c, r - 1);
                TryPush(state, seen, stack, cols, rows, c, r + 1);
            }

            float area = count * cellArea;
            if (area >= m_minClusterArea)
            {
                clusters.Add(
                    new Cluster
                    {
                        Level = level,
                        Area = area,
                        Center = sum / count,
                        Box = box,
                        Sample = hitPos[start],
                        Points = points,
                    }
                );
            }
        }
    }

    private void AnalyzeClusters(List<Cluster> clusters, List<Vector3> reachable)
    {
        List<Cluster> falsePositives = ReverifyAgainstNearestReachable(clusters, reachable);
        if (falsePositives.Count > 0)
        {
            var fpSb = new StringBuilder(
                $"맵 NavMesh 점검: 기준점 원거리 오탐으로 제외된 덩어리 {falsePositives.Count}개 "
                    + "(기준점에서는 부분경로만 나오지만 가까운 도달 지점에서는 완전 경로 확인됨)\n"
            );
            foreach (Cluster c in falsePositives)
            {
                fpSb.AppendLine($"  y{c.Level:F2}  {c.Area:F0}m² 중심 {Fmt(c.Center)}");
            }
            Debug.LogWarning(fpSb.ToString().TrimEnd());
        }

        if (clusters.Count == 0)
        {
            Debug.Log("맵 NavMesh 점검: 고립 덩어리 없음 — 검사한 전 층이 기준점과 이어져 있다");
            return;
        }

        var path = new NavMeshPath();

        int groupCount = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            if (clusters[i].Group >= 0)
            {
                continue;
            }

            clusters[i].Group = groupCount++;

            for (int j = i + 1; j < clusters.Count; j++)
            {
                if (clusters[j].Group >= 0)
                {
                    continue;
                }

                if (
                    NavMesh.CalculatePath(clusters[i].Sample, clusters[j].Sample, NavMesh.AllAreas, path)
                    && path.status == NavMeshPathStatus.PathComplete
                )
                {
                    clusters[j].Group = clusters[i].Group;
                }
            }
        }

        var groups = new List<List<Cluster>>(groupCount);
        for (int g = 0; g < groupCount; g++)
        {
            groups.Add(clusters.FindAll(c => c.Group == g));
        }

        groups.Sort((a, b) => TotalArea(b).CompareTo(TotalArea(a)));

        var sb = new StringBuilder(
            $"맵 NavMesh 점검: 고립 덩어리 {clusters.Count}개 — 서로 이어진 것끼리 묶으면 {groupCount}덩이\n"
        );

        for (int g = 0; g < groups.Count && g < k_maxClustersLogged; g++)
        {
            List<Cluster> group = groups[g];
            group.Sort((a, b) => a.Level.CompareTo(b.Level));

            sb.AppendLine(
                $"  [{g + 1}] 총 {TotalArea(group):F0}m² · {group.Count}조각 — {DescribeNearest(group, reachable)}"
            );

            foreach (Cluster c in group)
            {
                sb.AppendLine(
                    $"      y{c.Level:F2}  {c.Area:F0}m² 중심 {Fmt(c.Center)} "
                        + $"x[{c.Box.min.x:F1}~{c.Box.max.x:F1}] z[{c.Box.min.z:F1}~{c.Box.max.z:F1}]"
                );
            }
        }

        if (groups.Count > k_maxClustersLogged)
        {
            sb.AppendLine($"  … 외 {groups.Count - k_maxClustersLogged}덩이");
        }

        Debug.LogWarning(sb.ToString().TrimEnd());
    }

    private static List<Cluster> ReverifyAgainstNearestReachable(List<Cluster> clusters, List<Vector3> reachable)
    {
        var falsePositives = new List<Cluster>();
        if (reachable.Count == 0)
        {
            return falsePositives;
        }

        var path = new NavMeshPath();
        for (int i = clusters.Count - 1; i >= 0; i--)
        {
            Cluster c = clusters[i];

            Vector3 nearest = Vector3.zero;
            float best = float.MaxValue;
            foreach (Vector3 p in reachable)
            {
                float d = (p - c.Sample).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = p;
                }
            }

            if (
                NavMesh.CalculatePath(nearest, c.Sample, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete
            )
            {
                falsePositives.Add(c);
                clusters.RemoveAt(i);
            }
        }

        falsePositives.Reverse();
        return falsePositives;
    }

    private static string DescribeNearest(List<Cluster> group, List<Vector3> reachable)
    {
        if (reachable.Count == 0)
        {
            return "도달 가능 지점이 하나도 없다";
        }

        float best = float.MaxValue;
        Vector3 from = Vector3.zero;
        Vector3 to = Vector3.zero;

        foreach (Cluster c in group)
        {
            foreach (Vector3 q in c.Points)
            {
                foreach (Vector3 p in reachable)
                {
                    float d = (p - q).sqrMagnitude;
                    if (d >= best)
                    {
                        continue;
                    }

                    best = d;
                    to = p;
                    from = q;
                }
            }
        }

        float horizontal = new Vector2(to.x - from.x, to.z - from.z).magnitude;
        float vertical = to.y - from.y;
        string sign = vertical >= 0f ? "+" : string.Empty;

        return $"끊긴 곳 {Fmt(from)} ↔ 도달 가능 {Fmt(to)} — 수평 {horizontal:F2}m / 높이차 {sign}{vertical:F2}m";
    }

    private static float TotalArea(List<Cluster> group)
    {
        float sum = 0f;
        foreach (Cluster c in group)
        {
            sum += c.Area;
        }

        return sum;
    }

    private static void TryPush(int[] state, bool[] seen, Stack<int> stack, int cols, int rows, int c, int r)
    {
        if (c < 0 || r < 0 || c >= cols || r >= rows)
        {
            return;
        }

        int i = r * cols + c;
        if (state[i] != 0 || seen[i])
        {
            return;
        }

        seen[i] = true;
        stack.Push(i);
    }

    private void AuditAnchors(Vector3 origin)
    {
        string[] prefixes = SplitCsv(m_anchorPrefixes);
        if (prefixes.Length == 0)
        {
            return;
        }

        var path = new NavMeshPath();
        var unreachable = new List<string>();
        var floating = new List<string>();
        int total = 0;

        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (!StartsWithAny(t.name, prefixes))
            {
                continue;
            }

            total++;

            if (!NavMesh.SamplePosition(t.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                unreachable.Add($"{t.name} {Fmt(t.position)} — 2m 안에 NavMesh 없음");
            }
            else if (
                !NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                || path.status != NavMeshPathStatus.PathComplete
            )
            {
                unreachable.Add($"{t.name} {Fmt(t.position)} — 기준점에서 못 감 ({path.status})");
            }
            else if (hit.distance > k_anchorFloatTolerance)
            {
                floating.Add($"{t.name} {Fmt(t.position)} — NavMesh에서 {hit.distance:F2}m 떠 있음");
            }
        }

        if (total == 0)
        {
            Debug.LogWarning($"맵 NavMesh 점검: 앵커를 못 찾았다 (접두사 '{m_anchorPrefixes}')");
            return;
        }

        if (unreachable.Count == 0 && floating.Count == 0)
        {
            Debug.Log($"맵 NavMesh 점검: 앵커 {total}개 전부 정상");
            return;
        }

        if (unreachable.Count > 0)
        {
            Debug.LogError(Bullet($"맵 NavMesh 점검: 앵커 {total}개 중 {unreachable.Count}개 도달 불가", unreachable));
        }

        if (floating.Count > 0)
        {
            Debug.LogWarning(
                Bullet(
                    $"맵 NavMesh 점검: 앵커 {floating.Count}개가 {k_anchorFloatTolerance}m 넘게 떠 있다 (스폰 시 스냅되므로 대개 무해)",
                    floating
                )
            );
        }
    }

    private static string Bullet(string head, List<string> lines)
    {
        var sb = new StringBuilder(head + "\n");
        foreach (string line in lines)
        {
            sb.AppendLine($"  {line}");
        }

        return sb.ToString().TrimEnd();
    }

    private void AuditProbes(Vector3 origin)
    {
        if (string.IsNullOrWhiteSpace(m_probes))
        {
            return;
        }

        var path = new NavMeshPath();
        var sb = new StringBuilder("맵 NavMesh 점검: 지점 검사\n");
        bool anyFail = false;

        foreach (string raw in m_probes.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", System.StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = SplitCsv(line);
            if (
                parts.Length < 3
                || !TryFloat(parts[0], out float x)
                || !TryFloat(parts[1], out float y)
                || !TryFloat(parts[2], out float z)
            )
            {
                sb.AppendLine($"  ?    {line} — 'x,y,z[,불가]' 형식이 아니다");
                anyFail = true;
                continue;
            }

            bool expectBlocked = parts.Length >= 4 && parts[3] == "불가";
            var point = new Vector3(x, y, z);

            bool onMesh = NavMesh.SamplePosition(point, out NavMeshHit hit, m_sampleRadius, NavMesh.AllAreas);
            bool walkable =
                onMesh
                && NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete;

            bool pass = expectBlocked ? !walkable : walkable;
            anyFail |= !pass;

            string state = !onMesh
                ? $"{m_sampleRadius}m 안에 NavMesh 없음"
                : walkable
                    ? $"도달 가능 {Fmt(hit.position)}"
                    : "NavMesh는 있으나 못 감";

            sb.AppendLine(
                $"  {(pass ? "OK  " : "실패")} {Fmt(point)} 기대={(expectBlocked ? "불가" : "도달")} — {state}"
            );
        }

        if (anyFail)
        {
            Debug.LogError(sb.ToString().TrimEnd());
        }
        else
        {
            Debug.Log(sb.ToString().TrimEnd());
        }
    }

    private void LogSurfaces()
    {
        NavMeshSurface[] surfaces = FindObjectsByType<NavMeshSurface>(FindObjectsSortMode.None);
        if (surfaces.Length == 0)
        {
            Debug.LogError("맵 NavMesh 점검: 씬에 NavMeshSurface가 없다");
            return;
        }

        var sb = new StringBuilder($"맵 NavMesh 점검: NavMeshSurface {surfaces.Length}개\n");
        foreach (NavMeshSurface srf in surfaces)
        {
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(srf.agentTypeID);
            float voxel = srf.overrideVoxelSize ? srf.voxelSize : settings.agentRadius / 3f;

            sb.AppendLine(
                $"  {srf.name}: 복셀 {voxel:F3} · 최소영역 {srf.minRegionArea} · 수집 {srf.collectObjects}"
            );
            sb.AppendLine(
                $"    반지름 {settings.agentRadius} · 높이 {settings.agentHeight} · climb {settings.agentClimb} · slope {settings.agentSlope}"
            );
            sb.AppendLine($"    NavMeshData: {DescribeData(srf)}");
        }

        sb.Append($"  NavMeshLink {FindObjectsByType<NavMeshLink>(FindObjectsSortMode.None).Length}개");
        Debug.Log(sb.ToString());
    }

    private static string DescribeData(NavMeshSurface srf)
    {
        if (srf.navMeshData == null)
        {
            return "없음 — 굽지 않았다";
        }

        return AssetDatabase.Contains(srf.navMeshData)
            ? AssetDatabase.GetAssetPath(srf.navMeshData)
            : "⚠ 에셋이 아니다 — 씬을 저장하면 .unity가 바이너리가 된다";
    }

    private bool TryResolveOrigin(out Vector3 origin)
    {
        origin = Vector3.zero;
        Transform t = m_origin;

        if (t == null)
        {
            foreach (Transform candidate in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (candidate.name.StartsWith("PlayerSpawnPoint", System.StringComparison.Ordinal))
                {
                    t = candidate;
                    break;
                }
            }
        }

        if (t == null)
        {
            Debug.LogError("맵 NavMesh 점검: 기준점을 못 찾았다. 창에서 지정할 것");
            return false;
        }

        if (!NavMesh.SamplePosition(t.position, out NavMeshHit hit, k_originSampleRadius, NavMesh.AllAreas))
        {
            Debug.LogError($"맵 NavMesh 점검: 기준점 '{t.name}'{Fmt(t.position)} 근처에 NavMesh가 없다");
            return false;
        }

        m_origin = t;
        origin = hit.position;
        return true;
    }

    private static bool StartsWithAny(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (name.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Fmt(Vector3 v) => $"({v.x:F1}, {v.y:F1}, {v.z:F1})";

    private static string[] SplitCsv(string s)
    {
        var parts = new List<string>();
        foreach (string raw in s.Split(','))
        {
            string part = raw.Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        return parts.ToArray();
    }

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static IEnumerable<float> ParseFloats(string s)
    {
        foreach (string part in SplitCsv(s))
        {
            if (TryFloat(part, out float v))
            {
                yield return v;
            }
        }
    }
}
