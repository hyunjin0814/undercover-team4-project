using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 로봇 메시를 본 기준으로 부위별 서브메시 3개(EBodyPart 순서)로 갈라 굽는다.
/// 원본과 끊긴 사본이므로 원본 모델이 바뀌면 다시 돌려야 한다.
/// </summary>
public static class RobotPartMeshBaker
{
    private const string k_sourcePath = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/SM_Gen_Chr_Robot_01.prefab";
    private const string k_outputPath = "Assets/Meshes/SM_Gen_Chr_Robot_01_Parts.asset";

    private static readonly string[] s_headBones = { "Head", "Neck", "Eye", "Jaw" };
    private static readonly string[] s_legBones = { "Hips", "UpperLeg", "LowerLeg", "Ankle", "Ball", "Toe" };

    [MenuItem("Tools/Undercover/로봇 부위 메시 굽기 (#432)")]
    public static void Bake()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(k_sourcePath);
        if (source == null)
        {
            Debug.LogError($"[{nameof(RobotPartMeshBaker)}] 원본 프리팹을 찾지 못했습니다: {k_sourcePath}");
            return;
        }

        SkinnedMeshRenderer renderer = source.GetComponentInChildren<SkinnedMeshRenderer>();
        Mesh mesh = renderer != null ? renderer.sharedMesh : null;
        if (mesh == null)
        {
            Debug.LogError($"[{nameof(RobotPartMeshBaker)}] 스킨드 메시를 찾지 못했습니다.");
            return;
        }

        Mesh baked = Object.Instantiate(mesh);
        baked.name = "SM_Gen_Chr_Robot_01_Parts";

        EBodyPart[] vertexParts = ClassifyVertices(mesh, renderer.bones);
        int[] triangles = mesh.triangles;

        var buckets = new List<int>[3];
        for (int i = 0; i < buckets.Length; i++)
            buckets[i] = new List<int>();

        for (int t = 0; t < triangles.Length; t += 3)
        {
            EBodyPart part = MajorityPart(
                vertexParts[triangles[t]],
                vertexParts[triangles[t + 1]],
                vertexParts[triangles[t + 2]]
            );

            List<int> bucket = buckets[(int)part];
            bucket.Add(triangles[t]);
            bucket.Add(triangles[t + 1]);
            bucket.Add(triangles[t + 2]);
        }

        baked.subMeshCount = buckets.Length;
        for (int i = 0; i < buckets.Length; i++)
            baked.SetTriangles(buckets[i], i);

        if (!AssetDatabase.IsValidFolder("Assets/Meshes"))
            AssetDatabase.CreateFolder("Assets", "Meshes");

        AssetDatabase.DeleteAsset(k_outputPath);
        AssetDatabase.CreateAsset(baked, k_outputPath);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[{nameof(RobotPartMeshBaker)}] {k_outputPath} — 머리 {buckets[0].Count / 3} · "
                + $"상체 {buckets[1].Count / 3} · 하체 {buckets[2].Count / 3} 삼각형"
        );
    }

    private static EBodyPart[] ClassifyVertices(Mesh mesh, Transform[] bones)
    {
        BoneWeight[] weights = mesh.boneWeights;
        var parts = new EBodyPart[mesh.vertexCount];

        for (int i = 0; i < parts.Length; i++)
        {
            Transform bone = weights.Length > i ? bones[weights[i].boneIndex0] : null;
            parts[i] = PartOf(bone != null ? bone.name : string.Empty);
        }

        return parts;
    }

    private static EBodyPart PartOf(string boneName)
    {
        foreach (string prefix in s_headBones)
        {
            if (boneName.StartsWith(prefix))
                return EBodyPart.Head;
        }

        foreach (string prefix in s_legBones)
        {
            if (boneName.StartsWith(prefix))
                return EBodyPart.Legs;
        }

        return EBodyPart.Torso;
    }

    private static EBodyPart MajorityPart(EBodyPart a, EBodyPart b, EBodyPart c)
    {
        if (a == b || a == c)
            return a;

        return b == c ? b : a;
    }
}
