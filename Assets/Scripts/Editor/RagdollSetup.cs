using System.Collections.Generic;
using System.Text;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Ragdoll Wizard가 만든 래그돌에 레이어·충돌 매트릭스·키네마틱 초기화·물리 설정·관절 전처리 해제를 적용한다.
/// 메뉴: Tools > Ragdoll. 위저드 산출물(콜라이더·관절 축·질량)은 보존하고, 리그 검증에 실패하면 중단한다.
/// </summary>
public static class RagdollSetup
{
    private const int k_layerSlot = 10;

    private static readonly string[] s_collidesWith = { "Default" };

    private const bool k_enableProjection = false;

    public const string k_playerPrefab = "Assets/Prefabs/Player.prefab";

    public const string k_npcRigOwnerPath = "Model";

    public const string k_npcCitizenPrefab = "Assets/Prefabs/NPC/NPC_Citizen.prefab";

    public static readonly string[] s_npcPrefabs =
    {
        k_npcCitizenPrefab,
        "Assets/Prefabs/NPC/NPC_Citizen_Generic.prefab",
        "Assets/Prefabs/NPC/NPC_Rioter.prefab",
        "Assets/Prefabs/NPC/NPC_Streaker.prefab",
    };

    [MenuItem("Tools/Ragdoll/Finish Setup - Player")]
    public static void RunPlayer() => Run(k_playerPrefab, rigOwnerPath: "", stripHipsReplication: true);

    [MenuItem("Tools/Ragdoll/Finish Setup - NPC (전체)")]
    public static void RunAllNpc()
    {
        for (int i = 0; i < s_npcPrefabs.Length; i++)
            Run(s_npcPrefabs[i], k_npcRigOwnerPath, stripHipsReplication: true);
    }

    /// <summary>마무리를 실행한다. 서브 메뉴가 아니라 여기로 대상을 넘긴다 — 새 개체는 메뉴 한 줄만 늘리면 된다.</summary>
    public static void Run(string prefabPath, string rigOwnerPath, bool stripHipsReplication)
    {
        int layer = EnsureLayer();
        if (layer < 0)
            return;

        ConfigureLayerCollisions(layer);

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        if (root == null)
        {
            Debug.LogError($"[래그돌 셋업] 프리팹을 열 수 없다: {prefabPath}");
            return;
        }

        try
        {
            if (Apply(root, prefabPath, rigOwnerPath, layer, stripHipsReplication))
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static int EnsureLayer()
    {
        int existing = LayerMask.NameToLayer(RagdollRig.k_layerName);
        if (existing >= 0)
            return existing;

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets.Length == 0)
        {
            Debug.LogError("[래그돌 셋업] TagManager.asset을 열 수 없다 — 레이어를 만들지 못했다");
            return -1;
        }

        SerializedObject tagManager = new SerializedObject(assets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        if (layers == null || k_layerSlot >= layers.arraySize)
        {
            Debug.LogError("[래그돌 셋업] TagManager의 layers 배열을 읽을 수 없다");
            return -1;
        }

        SerializedProperty slot = layers.GetArrayElementAtIndex(k_layerSlot);
        if (!string.IsNullOrEmpty(slot.stringValue))
        {
            Debug.LogError(
                $"[래그돌 셋업] 레이어 슬롯 {k_layerSlot}이 이미 '{slot.stringValue}'로 쓰이고 있다 — "
                    + "k_layerSlot을 빈 슬롯으로 바꿔서 다시 실행할 것"
            );
            return -1;
        }

        slot.stringValue = RagdollRig.k_layerName;
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"[래그돌 셋업] 레이어 생성 — 슬롯 {k_layerSlot} = {RagdollRig.k_layerName}");

        return LayerMask.NameToLayer(RagdollRig.k_layerName);
    }

    private static void ConfigureLayerCollisions(int layer)
    {
        HashSet<int> allowed = new HashSet<int>();
        for (int i = 0; i < s_collidesWith.Length; i++)
        {
            int other = LayerMask.NameToLayer(s_collidesWith[i]);
            if (other < 0)
            {
                Debug.LogWarning($"[래그돌 셋업] 레이어 '{s_collidesWith[i]}'가 없다 — 건너뛴다");
                continue;
            }

            allowed.Add(other);
        }

        for (int other = 0; other < 32; other++)
            Physics.IgnoreLayerCollision(layer, other, !allowed.Contains(other));

        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(
            "ProjectSettings/DynamicsManager.asset"
        );
        if (assets.Length > 0)
            EditorUtility.SetDirty(assets[0]);
        AssetDatabase.SaveAssets();
    }

    private static bool Apply(
        GameObject root,
        string prefabPath,
        string rigOwnerPath,
        int layer,
        bool stripHipsReplication
    )
    {
        Transform rigOwner = ResolveRigOwner(root.transform, prefabPath, rigOwnerPath);
        if (rigOwner == null)
            return false;

        Transform boneRoot = rigOwner.Find(RagdollRig.k_defaultBoneRootName);
        if (boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌 셋업] 몸통 리그 '{RagdollRig.k_defaultBoneRootName}'를 "
                    + $"'{DescribeOwner(rigOwnerPath)}'의 직속 자식에서 찾지 못했다 — "
                    + "리그 위치가 다르면 rigOwnerPath를, 리그 이름이 바뀌었으면 "
                    + "RagdollRig.k_defaultBoneRootName을 맞출 것"
            );
            return false;
        }

        if (!CollectRagdollBodies(root, boneRoot, prefabPath, out List<Rigidbody> bodies))
            return false;

        StringBuilder report = new StringBuilder();
        report.AppendLine(
            $"[래그돌 셋업] {prefabPath} — 뼈 {bodies.Count}개 마무리 "
                + $"(리그 소유자 {DescribeOwner(rigOwnerPath)}, 레이어 {RagdollRig.k_layerName}, "
                + "전부 isKinematic=true · 콜라이더·관절·질량은 위저드 값 그대로)"
        );

        report.AppendLine(EnsureRig(rigOwner));

        for (int i = 0; i < bodies.Count; i++)
        {
            Rigidbody body = bodies[i];
            body.gameObject.layer = layer;

            body.isKinematic = true;
            body.useGravity = true;

            body.interpolation = RigidbodyInterpolation.None;

            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            CharacterJoint joint = body.GetComponent<CharacterJoint>();
            if (joint != null)
            {
                joint.enablePreprocessing = false;
                joint.enableProjection = k_enableProjection;
            }

            report.AppendLine(Describe(root.transform, body, joint));
        }

        if (stripHipsReplication)
            report.AppendLine(StripHipsReplication(bodies));

        Debug.Log(report.ToString());
        return true;
    }

    /// <summary>골반의 NetworkTransform·NetworkRigidbody를 제거한다(멱등).</summary>
    private static string StripHipsReplication(List<Rigidbody> bodies)
    {
        Rigidbody hips = null;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].GetComponent<CharacterJoint>() == null)
            {
                hips = bodies[i];
                break;
            }
        }

        if (hips == null)
            return "  ⚠ 골반 복제 해제 — 관절 없는 뼈를 찾지 못해 건너뛴다 (리그가 깨졌을 수 있다)";

        NetworkRigidbody netBody = hips.GetComponent<NetworkRigidbody>();
        if (netBody != null)
            Object.DestroyImmediate(netBody);

        NetworkTransform netTransform = hips.GetComponent<NetworkTransform>();
        if (netTransform != null)
            Object.DestroyImmediate(netTransform);

        if (netBody == null && netTransform == null)
            return $"  골반 복제({hips.name}) — 이미 없음";

        return $"  골반 복제({hips.name}) — 걷어냄 "
            + $"(NetworkTransform {(netTransform != null ? "삭제" : "없음")} / "
            + $"NetworkRigidbody {(netBody != null ? "삭제" : "없음")})";
    }

    private static Transform ResolveRigOwner(Transform root, string prefabPath, string rigOwnerPath)
    {
        if (string.IsNullOrEmpty(rigOwnerPath))
            return root;

        Transform owner = root.Find(rigOwnerPath);
        if (owner == null)
        {
            Debug.LogError(
                $"[래그돌 셋업] 리그 소유자 '{rigOwnerPath}'를 {prefabPath}에서 찾지 못했다 — "
                    + "프리팹 구조가 바뀌었으면 메뉴 호출부의 rigOwnerPath를 맞출 것"
            );
        }

        return owner;
    }

    private static string DescribeOwner(string rigOwnerPath) =>
        string.IsNullOrEmpty(rigOwnerPath) ? "프리팹 루트" : rigOwnerPath;

    /// <summary>리그 소유자에 RagdollRig와 RagdollRope를 보장한다(멱등).</summary>
    private static string EnsureRig(Transform rigOwner)
    {
        string rig = rigOwner.GetComponent<RagdollRig>() != null ? "이미 있음" : "새로 붙임";
        if (rigOwner.GetComponent<RagdollRig>() == null)
            rigOwner.gameObject.AddComponent<RagdollRig>();

        string rope = rigOwner.GetComponent<RagdollRope>() != null ? "이미 있음" : "새로 붙임";
        if (rigOwner.GetComponent<RagdollRope>() == null)
            rigOwner.gameObject.AddComponent<RagdollRope>();

        return $"  RagdollRig — {rig} / RagdollRope — {rope} ({rigOwner.name})";
    }

    /// <summary>CharacterJoint를 기준으로 위저드가 만든 래그돌 뼈 Rigidbody를 모은다.</summary>
    private static bool CollectRagdollBodies(
        GameObject root,
        Transform boneRoot,
        string prefabPath,
        out List<Rigidbody> bodies
    )
    {
        bodies = new List<Rigidbody>();

        HashSet<Rigidbody> set = new HashSet<Rigidbody>();
        CharacterJoint[] joints = root.GetComponentsInChildren<CharacterJoint>(true);
        for (int i = 0; i < joints.Length; i++)
        {
            Rigidbody own = joints[i].GetComponent<Rigidbody>();
            if (own != null)
                set.Add(own);
            if (joints[i].connectedBody != null)
                set.Add(joints[i].connectedBody);
        }

        if (set.Count == 0)
        {
            Debug.LogError(
                "[래그돌 셋업] CharacterJoint를 하나도 찾지 못했다 — 내장 Ragdoll Wizard를 먼저 돌릴 것.\n"
                    + $"{prefabPath}를 Prefab 모드로 열고 GameObject > 3D Object > Ragdoll… 에서 "
                    + $"{PathOf(root.transform, boneRoot)}/Hips 이하의 뼈를 지정한다."
            );
            return false;
        }

        List<string> outside = new List<string>();
        foreach (Rigidbody body in set)
        {
            if (body.transform.IsChildOf(boneRoot))
                bodies.Add(body);
            else
                outside.Add(PathOf(root.transform, body.transform));
        }

        if (outside.Count > 0)
        {
            Debug.LogError(
                "[래그돌 셋업] 몸통 리그 밖에 붙은 래그돌 뼈가 있어 중단한다 — 위저드에서 다른 리그"
                    + $"({PathOf(root.transform, boneRoot)}이 아닌 쪽)의 동명 뼈를 집은 것이다.\n"
                    + string.Join("\n", outside)
                    + $"\n\n해당 뼈의 Rigidbody·Collider·CharacterJoint를 지우고, {prefabPath}의 "
                    + $"'{PathOf(root.transform, boneRoot)}/Hips' 이하만 지정해 위저드를 다시 돌릴 것."
            );
            bodies.Clear();
            return false;
        }

        bodies.Sort(
            (a, b) =>
                PathOf(root.transform, a.transform)
                    .CompareTo(PathOf(root.transform, b.transform))
        );
        return true;
    }

    private static string Describe(Transform root, Rigidbody body, CharacterJoint joint)
    {
        Collider collider = body.GetComponent<Collider>();
        string shape = collider switch
        {
            CapsuleCollider capsule =>
                $"Capsule r={capsule.radius:F3} h={capsule.height:F3} "
                    + $"원통={(capsule.height - capsule.radius * 2f):F3}",
            BoxCollider box => $"Box {box.size.x:F3}×{box.size.y:F3}×{box.size.z:F3}",
            SphereCollider sphere => $"Sphere r={sphere.radius:F3}",
            null => "콜라이더 없음",
            _ => collider.GetType().Name,
        };

        string parent = joint == null
            ? "루트(관절 없음)"
            : joint.connectedBody == null
                ? "연결 끊김!"
                : joint.connectedBody.name;

        return $"  {PathOf(root, body.transform)}  |  질량 {body.mass:F1}  |  {shape}  |  부모 {parent}";
    }

    private static string PathOf(Transform root, Transform target)
    {
        string path = target.name;
        Transform cursor = target;
        while (cursor.parent != null && cursor != root)
        {
            cursor = cursor.parent;
            path = cursor.name + "/" + path;
        }

        return path;
    }
}
