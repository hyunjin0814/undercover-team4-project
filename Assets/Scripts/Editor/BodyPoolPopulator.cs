using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Apocalypse·PoliceStation 팩 바디를 NPC_Citizen_Generic 아래 Generic 스켈레톤으로 다시 바인딩해 붙인다.
/// 메뉴: Tools/바디 풀 배선. 다시 돌려도 안전하다.
/// </summary>
public static class BodyPoolPopulator
{
    private const string k_targetPrefab = "Assets/Prefabs/NPC/NPC_Citizen_Generic.prefab";

    private const string k_genericPrefix = "SM_Gen_";

    private readonly struct Pack
    {
        public readonly string DonorPrefab;

        public readonly string Rename;

        public readonly string[] Excluded;

        public Pack(string donorPrefab, string rename, params string[] excluded)
        {
            DonorPrefab = donorPrefab;
            Rename = rename;
            Excluded = excluded;
        }
    }

    private static readonly Pack[] s_packs =
    {
        new Pack(
            "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/SM_Chr_Biker_Male_01.prefab",
            "SM_Apo_",
            "SM_Chr_Zombie_Male_01",
            "SM_Chr_Zombie_Male_02",
            "SM_Chr_Zombie_Female_01",
            "SM_Chr_Zombie_Female_02"
        ),
        new Pack(
            "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/SM_Chr_Officer_Male_01.prefab",
            "SM_Pol_"
        ),
    };

    [MenuItem("Tools/바디 풀 배선")]
    private static void Populate()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(k_targetPrefab);
        if (root == null)
        {
            Debug.LogError($"[바디 풀] {k_targetPrefab}을 열지 못했다");
            return;
        }

        try
        {
            Transform model = root.transform.Find("Model");
            Transform skeleton = model != null ? model.Find("Root") : null;
            if (skeleton == null)
            {
                Debug.LogError("[바디 풀] Model/Root 스켈레톤을 찾지 못했다 — 프리팹 계층이 바뀌었는지 확인할 것");
                return;
            }

            int removed = RemoveForeignBodies(model);

            var bones = new Dictionary<string, Transform>();
            foreach (Transform bone in skeleton.GetComponentsInChildren<Transform>(true))
                bones[bone.name] = bone;

            var missing = new List<string>();
            var log = new System.Text.StringBuilder();
            int added = 0;
            foreach (Pack pack in s_packs)
                added += AddPack(pack, model, skeleton, bones, missing, log);

            if (missing.Count > 0)
            {
                Debug.LogError(
                    $"[바디 풀] Generic 스켈레톤에서 짝을 못 찾은 본 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}"
                );
                return;
            }

            PrefabUtility.SaveAsPrefabAsset(root, k_targetPrefab);
            Debug.Log(
                $"[바디 풀] 팩 바디 {added}개 배선 완료 (이전 {removed}개 걷어냄){log}"
            );
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>지난번에 붙인 팩 바디를 걷어낸다 — 원래 바디(<c>SM_Gen_</c>)는 건드리지 않는다.</summary>
    private static int RemoveForeignBodies(Transform model)
    {
        var doomed = new List<GameObject>();
        foreach (SkinnedMeshRenderer body in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!body.name.StartsWith(k_genericPrefix))
                doomed.Add(body.gameObject);
        }

        foreach (GameObject go in doomed)
            Object.DestroyImmediate(go);
        return doomed.Count;
    }

    private static int AddPack(
        in Pack pack,
        Transform model,
        Transform skeleton,
        Dictionary<string, Transform> bones,
        List<string> missing,
        System.Text.StringBuilder log
    )
    {
        GameObject donor = AssetDatabase.LoadAssetAtPath<GameObject>(pack.DonorPrefab);
        if (donor == null)
        {
            missing.Add($"기증자 프리팹 없음: {pack.DonorPrefab}");
            return 0;
        }

        var excluded = new HashSet<string>(pack.Excluded);
        int added = 0;
        foreach (SkinnedMeshRenderer source in donor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (source.sharedMesh == null || excluded.Contains(source.name))
                continue;

            string name = pack.Rename + StripPrefix(source.name);
            var go = new GameObject(name);
            go.transform.SetParent(model, false);
            go.layer = model.gameObject.layer;

            SkinnedMeshRenderer body = go.AddComponent<SkinnedMeshRenderer>();
            body.sharedMesh = source.sharedMesh;
            body.sharedMaterials = source.sharedMaterials;
            body.quality = source.quality;
            body.updateWhenOffscreen = source.updateWhenOffscreen;
            body.rootBone =
                source.rootBone != null && bones.TryGetValue(source.rootBone.name, out Transform packRoot)
                    ? packRoot
                    : skeleton;
            body.localBounds = source.localBounds;

            Transform[] sourceBones = source.bones;
            var mapped = new Transform[sourceBones.Length];
            for (int i = 0; i < sourceBones.Length; i++)
                mapped[i] = ResolveBone(sourceBones[i], bones, name, missing);
            body.bones = mapped;

            go.SetActive(false);
            added++;
            log.Append("\n  ").Append(name);
        }
        return added;
    }

    /// <summary>팩 본에 대응하는 Generic 본을 찾는다. 이름에 좌우가 없으면 조상 Hand_L/R로 판정한다.</summary>
    private static Transform ResolveBone(
        Transform sourceBone,
        Dictionary<string, Transform> bones,
        string bodyName,
        List<string> missing
    )
    {
        if (sourceBone == null)
            return null;

        if (bones.TryGetValue(sourceBone.name, out Transform direct))
            return direct;

        for (Transform t = sourceBone; t != null; t = t.parent)
        {
            if (!t.name.StartsWith("Hand_"))
                continue;

            string sided = StripDuplicateSuffix(sourceBone.name) + t.name.Substring(t.name.Length - 2);
            if (bones.TryGetValue(sided, out Transform resolved))
                return resolved;
            break;
        }

        missing.Add($"{bodyName}: {sourceBone.name}");
        return null;
    }

    /// <summary>"Finger_01 1" → "Finger_01" (유니티가 중복 이름에 붙인 꼬리).</summary>
    private static string StripDuplicateSuffix(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name.Substring(0, space) : name;
    }

    /// <summary>"SM_Chr_Biker_Male_01" → "Chr_Biker_Male_01" — 팩 접두사 자리를 비운다.</summary>
    private static string StripPrefix(string name) =>
        name.StartsWith("SM_") ? name.Substring("SM_".Length) : name;
}
