using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 현재 플레이어 모델의 오른팔에서 1인칭 팔 메시·프리팹을 다시 생성한다.
/// 메뉴: Tools/FP Arm/Regenerate Right Arm From Player Model.
/// </summary>
public static class FPArmGenerator
{
    private const string k_playerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string k_dir = "Assets/Prefabs/FPArm";
    private const string k_meshPath = k_dir + "/FPArm_Right_Mesh.asset";
    private const string k_armPrefabPath = k_dir + "/FPArm_Right.prefab";
    private const string k_shoulderBone = "Shoulder_R";

    [MenuItem("Tools/FP Arm/Regenerate Right Arm From Player Model")]
    public static void Regenerate()
    {
        GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(k_playerPrefabPath);
        if (player == null)
        {
            Debug.LogError($"[FPArmGenerator] {k_playerPrefabPath} 를 찾지 못했다.");
            return;
        }

        SkinnedMeshRenderer body = FindBodyRenderer(player);
        if (body == null || body.sharedMesh == null)
        {
            Debug.LogError(
                "[FPArmGenerator] 플레이어 몸 SkinnedMeshRenderer를 찾지 못했다 (FPArm_Right 바깥의 SMR)."
            );
            return;
        }

        bool isNewMesh = false;
        Mesh armMesh = AssetDatabase.LoadAssetAtPath<Mesh>(k_meshPath);
        if (armMesh == null)
        {
            armMesh = new Mesh();
            isNewMesh = true;
        }
        armMesh.name = "FPArm_Right_Mesh";

        if (!FillArmMesh(body, armMesh))
        {
            if (isNewMesh)
            {
                Object.DestroyImmediate(armMesh);
            }
            return;
        }

        if (!AssetDatabase.IsValidFolder(k_dir))
        {
            AssetDatabase.CreateFolder("Assets/Prefabs", "FPArm");
        }
        if (isNewMesh)
        {
            AssetDatabase.CreateAsset(armMesh, k_meshPath);
        }
        AssetDatabase.SaveAssets();

        BuildArmPrefab(player, armMesh);

        Debug.Log(
            $"[FPArmGenerator] 재생성 완료 — verts={armMesh.vertexCount}, tris={armMesh.triangles.Length / 3}. "
                + $"{k_armPrefabPath} / {k_meshPath}"
        );
    }

    private static SkinnedMeshRenderer FindBodyRenderer(GameObject root)
    {
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            bool underFPArm = false;
            for (Transform t = smr.transform; t != null; t = t.parent)
            {
                if (t.name == "FPArm_Right")
                {
                    underFPArm = true;
                    break;
                }
            }
            if (!underFPArm)
            {
                return smr;
            }
        }
        return null;
    }

    private static bool FillArmMesh(SkinnedMeshRenderer body, Mesh target)
    {
        Mesh src = body.sharedMesh;
        Transform[] bones = body.bones;
        Transform shoulder = System.Array.Find(bones, b => b != null && b.name == k_shoulderBone);
        if (shoulder == null)
        {
            Debug.LogError(
                $"[FPArmGenerator] 리그에서 {k_shoulderBone} 본을 찾지 못했다. 다른 리그면 k_shoulderBone을 맞춰라."
            );
            return false;
        }

        var armSet = new HashSet<Transform>(shoulder.GetComponentsInChildren<Transform>());
        var armBoneIndices = new HashSet<int>();
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] != null && armSet.Contains(bones[i]))
            {
                armBoneIndices.Add(i);
            }
        }

        BoneWeight[] weights = src.boneWeights;
        bool[] isArm = new bool[src.vertexCount];
        for (int v = 0; v < src.vertexCount; v++)
        {
            BoneWeight w = weights[v];
            int dominant = w.boneIndex0;
            float max = w.weight0;
            if (w.weight1 > max)
            {
                max = w.weight1;
                dominant = w.boneIndex1;
            }
            if (w.weight2 > max)
            {
                max = w.weight2;
                dominant = w.boneIndex2;
            }
            if (w.weight3 > max)
            {
                dominant = w.boneIndex3;
            }
            isArm[v] = armBoneIndices.Contains(dominant);
        }

        Vector3[] verts = src.vertices;
        Vector3[] normals = src.normals;
        Vector4[] tangents = src.tangents;
        Vector2[] uvs = src.uv;
        bool hasNormals = normals != null && normals.Length == src.vertexCount;
        bool hasTangents = tangents != null && tangents.Length == src.vertexCount;
        bool hasUv = uvs != null && uvs.Length == src.vertexCount;

        var remap = new Dictionary<int, int>();
        var newVerts = new List<Vector3>();
        var newNormals = new List<Vector3>();
        var newTangents = new List<Vector4>();
        var newUvs = new List<Vector2>();
        var newWeights = new List<BoneWeight>();
        var newTris = new List<int>();

        for (int s = 0; s < src.subMeshCount; s++)
        {
            int[] tris = src.GetTriangles(s);
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t],
                    b = tris[t + 1],
                    c = tris[t + 2];
                if (!isArm[a] || !isArm[b] || !isArm[c])
                {
                    continue;
                }
                foreach (int oldIndex in new[] { a, b, c })
                {
                    if (!remap.TryGetValue(oldIndex, out int newIndex))
                    {
                        newIndex = newVerts.Count;
                        remap[oldIndex] = newIndex;
                        newVerts.Add(verts[oldIndex]);
                        if (hasNormals)
                            newNormals.Add(normals[oldIndex]);
                        if (hasTangents)
                            newTangents.Add(tangents[oldIndex]);
                        if (hasUv)
                            newUvs.Add(uvs[oldIndex]);
                        newWeights.Add(weights[oldIndex]);
                    }
                    newTris.Add(newIndex);
                }
            }
        }

        if (newTris.Count == 0)
        {
            Debug.LogError("[FPArmGenerator] 팔 삼각형이 0개다 — 본 이름·스킨 가중치를 확인하라.");
            return false;
        }

        target.Clear();
        target.indexFormat = IndexFormat.UInt16;
        target.SetVertices(newVerts);
        if (hasNormals)
            target.SetNormals(newNormals);
        if (hasTangents)
            target.SetTangents(newTangents);
        if (hasUv)
            target.SetUVs(0, newUvs);
        target.boneWeights = newWeights.ToArray();
        target.bindposes = src.bindposes;
        target.SetTriangles(newTris, 0);
        if (!hasNormals)
            target.RecalculateNormals();
        target.RecalculateBounds();
        return true;
    }

    private static void BuildArmPrefab(GameObject player, Mesh armMesh)
    {
        GameObject clone = Object.Instantiate(player);
        clone.name = "TMP_FPArmSource";
        try
        {
            SkinnedMeshRenderer arm = FindBodyRenderer(clone);
            arm.sharedMesh = armMesh;
            arm.shadowCastingMode = ShadowCastingMode.Off;
            arm.receiveShadows = false;

            Transform skeletonTop = arm.rootBone;
            while (skeletonTop != null && skeletonTop.parent != clone.transform)
            {
                skeletonTop = skeletonTop.parent;
            }
            if (skeletonTop == null)
            {
                Debug.LogError(
                    "[FPArmGenerator] 팔 SkinnedMeshRenderer에 rootBone이 없다 — "
                        + "SkinnedMeshRenderer의 Root Bone 할당을 확인하라. 프리팹 생성을 건너뛴다."
                );
                return;
            }

            GameObject newRoot = new GameObject("FPArm_Right");
            skeletonTop.SetParent(newRoot.transform, true);
            arm.transform.SetParent(newRoot.transform, true);

            Transform shoulder = System.Array.Find(
                arm.bones,
                b => b != null && b.name == k_shoulderBone
            );
            if (shoulder != null)
            {
                skeletonTop.position -= shoulder.position;
            }

            PrefabUtility.SaveAsPrefabAsset(newRoot, k_armPrefabPath);
            Object.DestroyImmediate(newRoot);
        }
        finally
        {
            Object.DestroyImmediate(clone);
        }
    }
}
