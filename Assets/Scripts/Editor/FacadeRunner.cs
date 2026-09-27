using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 씬에서 고른 두 점을 잇는 선을 따라 건물 파사드 한 줄을 랜덤 적층으로 깐다.
/// 메뉴: Tools/파사드 생성기. 조각은 폴더·접두사 규칙으로 자동 수집한다.
/// </summary>
public static class FacadeRunner
{
    public class Settings
    {
        public string PieceFolder = "Assets/Imported/Synty/PolygonSciFiCity/Prefabs/Buildings";

        public float FloorHeight = 3f;
        public int MinFloors = 3;
        public int MaxFloors = 7;
        public float MinWidthScale = 1f;
        public float MaxWidthScale = 2.5f;
        public float DepthScale = 1.25f;
        public float Gap = 0.1f;
        public int MaxSameFloors = 2;
        public float RoofVolumeHeight = 2f;
        public int Seed = 0;
    }

    private static readonly string[] k_groundPrefixes = { "SM_Bld_Section_Door_" };
    private static readonly string[] k_middlePrefixes = { "SM_Bld_Section_Window_" };
    private static readonly string[] k_topPrefixes = { "SM_Bld_Section_Industrial_", "SM_Bld_Section_Wall_", "SM_Bld_Section_Grid_" };

    [MenuItem("Tools/파사드 생성기")]
    private static void OpenWindow() => FacadeRunnerWindow.Open();

    /// <summary>start→end 선을 따라 건물을 이어 붙이고 생성된 묶음의 루트를 돌려준다.</summary>
    public static GameObject Build(Vector3 start, Vector3 end, Settings settings, Transform parent)
    {
        List<GameObject> ground = Collect(settings.PieceFolder, k_groundPrefixes);
        List<GameObject> middle = Collect(settings.PieceFolder, k_middlePrefixes);
        List<GameObject> top = Collect(settings.PieceFolder, k_topPrefixes);
        if (ground.Count == 0 || middle.Count == 0 || top.Count == 0)
        {
            Debug.LogError($"FacadeRunner: '{settings.PieceFolder}'에서 Section 조각을 찾지 못했다");
            return null;
        }

        Vector3 flatStart = new Vector3(start.x, 0f, start.z);
        Vector3 flatEnd = new Vector3(end.x, 0f, end.z);
        Vector3 dir = flatEnd - flatStart;
        float length = dir.magnitude;
        if (length < 0.01f)
        {
            Debug.LogError("FacadeRunner: 시작점과 끝점이 같은 자리다");
            return null;
        }

        dir /= length;
        Vector3 outward = new Vector3(dir.z, 0f, -dir.x);
        float yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;

        Random.InitState(settings.Seed);
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("파사드 생성");
        int undoGroup = Undo.GetCurrentGroup();

        GameObject root = new GameObject("Facade");
        Undo.RegisterCreatedObjectUndo(root, "파사드 생성");
        if (parent != null) root.transform.SetParent(parent, false);
        root.transform.position = flatStart;

        float baseWidth = ModalWidth(middle);
        KeepWidth(ground, baseWidth);
        KeepWidth(middle, baseWidth);
        KeepWidth(top, baseWidth);
        KeepDepth(middle, ModalDepth(middle));
        if (ground.Count == 0 || middle.Count == 0 || top.Count == 0)
        {
            Debug.LogError($"FacadeRunner: 폭 {baseWidth:0.00}m 조각이 없다");
            return null;
        }

        float cursor = 0f;
        int built = 0;

        while (length - cursor > baseWidth * settings.MinWidthScale * 0.5f)
        {
            float widthScale = Mathf.Round(Random.Range(settings.MinWidthScale, settings.MaxWidthScale) * 2f) / 2f;
            float width = baseWidth * widthScale;
            if (cursor + width > length)
            {
                widthScale = Mathf.Floor((length - cursor) / baseWidth * 2f) / 2f;
                if (widthScale < 0.5f) break;
                width = baseWidth * widthScale;
            }

            int floors = Random.Range(settings.MinFloors, settings.MaxFloors + 1);
            Vector3 center = flatStart + dir * (cursor + width * 0.5f);
            GameObject building = new GameObject($"Bld_{built:00}");
            Undo.RegisterCreatedObjectUndo(building, "파사드 생성");
            building.transform.SetParent(root.transform, true);
            building.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yaw, 0f));

            GameObject groundPiece = ground[Random.Range(0, ground.Count)];
            GameObject topPiece = top[Random.Range(0, top.Count)];
            GameObject middlePiece = middle[Random.Range(0, middle.Count)];
            int sameLeft = Random.Range(1, settings.MaxSameFloors + 1);

            for (int floor = 0; floor < floors; floor++)
            {
                if (floor > 0 && floor < floors - 1 && --sameLeft <= 0)
                {
                    middlePiece = PickOther(middle, middlePiece);
                    sameLeft = Random.Range(1, settings.MaxSameFloors + 1);
                }

                GameObject source = floor == 0 ? groundPiece : (floor == floors - 1 ? topPiece : middlePiece);
                GameObject piece = (GameObject)PrefabUtility.InstantiatePrefab(source, building.transform);
                piece.transform.localPosition = new Vector3(0f, floor * settings.FloorHeight, 0f);
                piece.transform.localRotation = Quaternion.identity;
                piece.transform.localScale = new Vector3(widthScale, 1f, settings.DepthScale);
            }

            SnapEdge(building.transform, -dir, flatStart + dir * cursor);
            SnapEdge(building.transform, outward, flatStart);
            float actualWidth = ExtentAlong(building.transform, dir) * 2f;
            if (cursor + actualWidth > length + 0.1f)
            {
                Undo.DestroyObjectImmediate(building);
                break;
            }

            MarkStatic(building);
            AddRoofVolume(building.transform, settings.RoofVolumeHeight);

            cursor += actualWidth + settings.Gap;
            built++;
        }

        Debug.Log($"[파사드] {built}채 · 길이 {length:0.0}m · 벽면 방향 {yaw:0}°", root);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;
        return root;
    }

    /// <summary>건물 옥상에 실측 풋프린트 크기의 Not Walkable NavMesh 볼륨을 얹는다.</summary>
    private static void AddRoofVolume(Transform building, float height)
    {
        if (height <= 0f) return;

        Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        Bounds local = new Bounds();
        bool init = false;
        foreach (Renderer renderer in renderers)
        {
            Bounds b = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? b.min.x : b.max.x,
                    (corner & 2) == 0 ? b.min.y : b.max.y,
                    (corner & 4) == 0 ? b.min.z : b.max.z);
                Vector3 localPoint = building.InverseTransformPoint(point);
                if (!init) { local = new Bounds(localPoint, Vector3.zero); init = true; }
                else local.Encapsulate(localPoint);
            }
        }

        GameObject volume = new GameObject("NavMesh Modifier Volume");
        Undo.RegisterCreatedObjectUndo(volume, "파사드 생성");
        volume.transform.SetParent(building, false);
        volume.transform.localPosition = new Vector3(local.center.x, local.max.y, local.center.z);
        volume.transform.localRotation = Quaternion.identity;

        Unity.AI.Navigation.NavMeshModifierVolume modifier = volume.AddComponent<Unity.AI.Navigation.NavMeshModifierVolume>();
        modifier.area = 1;
        modifier.center = Vector3.zero;
        modifier.size = new Vector3(local.size.x + 0.4f, height, local.size.z + 0.4f);
    }

    /// <summary>axis 방향으로 가장 튀어나온 면이 planePoint를 지나는 평면에 닿도록 통째로 민다.</summary>
    private static void SnapEdge(Transform building, Vector3 axis, Vector3 planePoint)
    {
        if (!TryMeasure(building, out Bounds b)) return;
        float face = Vector3.Dot(b.center, axis) + ExtentAlong(building, axis);
        building.position += axis * (Vector3.Dot(planePoint, axis) - face);
    }

    private static float ExtentAlong(Transform building, Vector3 axis)
    {
        if (!TryMeasure(building, out Bounds b)) return 0f;
        return Mathf.Abs(axis.x) * b.extents.x + Mathf.Abs(axis.z) * b.extents.z;
    }

    private static bool TryMeasure(Transform building, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return true;
    }

    private static float ModalWidth(List<GameObject> pool)
    {
        Dictionary<float, int> counts = new Dictionary<float, int>();
        foreach (GameObject go in pool)
        {
            float w = Mathf.Round(MeasureBounds(go).size.x * 100f) / 100f;
            counts[w] = counts.ContainsKey(w) ? counts[w] + 1 : 1;
        }

        float best = 5f;
        int bestCount = 0;
        foreach (KeyValuePair<float, int> pair in counts)
        {
            if (pair.Value <= bestCount) continue;
            best = pair.Key;
            bestCount = pair.Value;
        }
        return best;
    }

    private static void KeepWidth(List<GameObject> pool, float width)
    {
        pool.RemoveAll(go => Mathf.Abs(MeasureBounds(go).size.x - width) > 0.15f);
    }

    private static GameObject PickOther(List<GameObject> pool, GameObject current)
    {
        if (pool.Count <= 1) return pool[0];

        GameObject picked = current;
        while (picked == current) picked = pool[Random.Range(0, pool.Count)];
        return picked;
    }

    private static float ModalDepth(List<GameObject> pool)
    {
        Dictionary<float, int> counts = new Dictionary<float, int>();
        foreach (GameObject go in pool)
        {
            float d = Mathf.Round(MeasureBounds(go).size.z * 100f) / 100f;
            counts[d] = counts.ContainsKey(d) ? counts[d] + 1 : 1;
        }

        float best = 0f;
        int bestCount = 0;
        foreach (KeyValuePair<float, int> pair in counts)
        {
            if (pair.Value <= bestCount) continue;
            best = pair.Key;
            bestCount = pair.Value;
        }
        return best;
    }

    private static void KeepDepth(List<GameObject> pool, float depth)
    {
        if (depth <= 0f) return;
        pool.RemoveAll(go => Mathf.Abs(MeasureBounds(go).size.z - depth) > 0.15f);
    }

    private static Bounds MeasureBounds(GameObject prefab)
    {
        MeshFilter[] filters = prefab.GetComponentsInChildren<MeshFilter>(true);
        Bounds b = new Bounds();
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].sharedMesh == null) continue;
            if (b.size == Vector3.zero) b = filters[i].sharedMesh.bounds;
            else b.Encapsulate(filters[i].sharedMesh.bounds);
        }
        return b;
    }

    private static void MarkStatic(GameObject go)
    {
        GameObjectUtility.SetStaticEditorFlags(go,
            StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
        {
            GameObjectUtility.SetStaticEditorFlags(child.gameObject,
                StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
        }
    }

    private static List<GameObject> Collect(string folder, string[] prefixes)
    {
        List<GameObject> found = new List<GameObject>();
        if (!AssetDatabase.IsValidFolder(folder)) return found;

        foreach (string guid in AssetDatabase.FindAssets("t:prefab", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (string prefix in prefixes)
            {
                if (!name.StartsWith(prefix)) continue;
                found.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                break;
            }
        }
        return found;
    }
}

public class FacadeRunnerWindow : EditorWindow
{
    private static readonly FacadeRunner.Settings s_settings = new FacadeRunner.Settings();

    public static void Open() => GetWindow<FacadeRunnerWindow>("파사드 생성기").minSize = new Vector2(360f, 300f);

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "씬에서 시작·끝 오브젝트 2개를 고른 뒤 생성. 시작→끝 진행 방향의 오른쪽이 벽면(도로 쪽)이다.",
            MessageType.Info);

        s_settings.FloorHeight = EditorGUILayout.FloatField("층고", s_settings.FloorHeight);
        s_settings.MinFloors = EditorGUILayout.IntField("최소 층수", s_settings.MinFloors);
        s_settings.MaxFloors = EditorGUILayout.IntField("최대 층수", s_settings.MaxFloors);
        s_settings.MinWidthScale = EditorGUILayout.FloatField("최소 폭 배율", s_settings.MinWidthScale);
        s_settings.MaxWidthScale = EditorGUILayout.FloatField("최대 폭 배율", s_settings.MaxWidthScale);
        s_settings.DepthScale = EditorGUILayout.FloatField("깊이 배율", s_settings.DepthScale);
        s_settings.Gap = EditorGUILayout.FloatField("건물 간격", s_settings.Gap);
        s_settings.MaxSameFloors = EditorGUILayout.IntField("같은 창문 최대 연속 층", s_settings.MaxSameFloors);
        s_settings.RoofVolumeHeight = EditorGUILayout.FloatField("옥상 차단 볼륨 두께", s_settings.RoofVolumeHeight);
        s_settings.Seed = EditorGUILayout.IntField("시드", s_settings.Seed);

        EditorGUILayout.Space();
        s_settings.PieceFolder = EditorGUILayout.TextField("조각 폴더", s_settings.PieceFolder);

        EditorGUILayout.Space();
        GameObject[] picked = Selection.gameObjects;
        using (new EditorGUI.DisabledScope(picked.Length != 2))
        {
            if (GUILayout.Button("선택한 두 점 사이에 파사드 생성", GUILayout.Height(30f)))
            {
                FacadeRunner.Build(picked[0].transform.position, picked[1].transform.position, s_settings,
                    picked[0].transform.parent);
            }
        }

        if (picked.Length != 2)
        {
            EditorGUILayout.LabelField($"오브젝트 2개를 골라야 한다 (현재 {picked.Length}개)");
        }
    }
}
