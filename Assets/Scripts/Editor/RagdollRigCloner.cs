using System.Collections.Generic;
using System.Text;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// 완성된 래그돌 리그를 같은 뼈대를 쓰는 다른 프리팹으로 복제하고 마무리까지 적용한다.
/// 메뉴: Tools > Ragdoll > Clone Rig. 뼈 좌표가 하나라도 어긋나면 그 프리팹은 건너뛴다.
/// </summary>
public static class RagdollRigCloner
{
    private const float k_boneMatchTolerance = 0.001f;

    [MenuItem("Tools/Ragdoll/Clone Rig - NPC (Citizen 기준)")]
    public static void CloneToNpcPrefabs()
    {
        GameObject source = PrefabUtility.LoadPrefabContents(RagdollSetup.k_npcCitizenPrefab);
        if (source == null)
        {
            Debug.LogError($"[래그돌 복제] 원본을 열 수 없다: {RagdollSetup.k_npcCitizenPrefab}");
            return;
        }

        List<string> cloned = new List<string>();
        try
        {
            Transform sourceBoneRoot = ResolveBoneRoot(source, RagdollSetup.k_npcCitizenPrefab);
            if (sourceBoneRoot == null)
                return;

            List<Transform> sourceBones = CollectRagdollBones(source, sourceBoneRoot);
            if (sourceBones.Count == 0)
            {
                Debug.LogError(
                    "[래그돌 복제] 원본에서 래그돌 뼈를 찾지 못했다 — "
                        + $"{RagdollSetup.k_npcCitizenPrefab}에 리그가 있는지 확인할 것"
                );
                return;
            }

            for (int i = 0; i < RagdollSetup.s_npcPrefabs.Length; i++)
            {
                string targetPath = RagdollSetup.s_npcPrefabs[i];
                if (targetPath == RagdollSetup.k_npcCitizenPrefab)
                    continue;

                if (CloneInto(sourceBoneRoot, sourceBones, targetPath))
                    cloned.Add(targetPath);
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(source);
        }

        for (int i = 0; i < cloned.Count; i++)
            RagdollSetup.Run(cloned[i], RagdollSetup.k_npcRigOwnerPath, stripHipsReplication: true);
    }

    /// <summary>플레이어 프리팹 안에서 시체 리그(Corpse/Root)를 살아있는 리그(Root)로 복제한다.</summary>
    [MenuItem("Tools/Ragdoll/Clone Rig - Player (Corpse → 살아있는 리그)")]
    public static void CloneToPlayerLiveRig()
    {
        string path = RagdollSetup.k_playerPrefab;
        GameObject prefab = PrefabUtility.LoadPrefabContents(path);
        if (prefab == null)
        {
            Debug.LogError($"[래그돌 복제] 프리팹을 열 수 없다: {path}");
            return;
        }

        try
        {
            string boneRootName = RagdollRig.k_defaultBoneRootName;
            Transform source = prefab.transform.Find($"Corpse/{boneRootName}");
            Transform target = prefab.transform.Find(boneRootName);

            if (source == null || target == null)
            {
                Debug.LogError(
                    $"[래그돌 복제] 리그를 찾지 못했다 — 시체 리그 'Corpse/{boneRootName}'="
                        + $"{(source == null ? "없음" : "있음")}, 살아있는 리그 '{boneRootName}'="
                        + $"{(target == null ? "없음" : "있음")}. 이미 단일화됐다면 이 메뉴는 쓸 일이 없다"
                );
                return;
            }

            List<Transform> sourceBones = CollectRagdollBones(prefab, source);
            if (sourceBones.Count == 0)
            {
                Debug.LogError("[래그돌 복제] 시체 리그에서 래그돌 뼈를 찾지 못했다 — 원본이 비었다");
                return;
            }

            if (!MapBones(source, sourceBones, target, path, out List<Transform> targetBones))
                return;

            StripExistingRagdoll(targetBones);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyIfPresent(sourceBones[i].GetComponent<Rigidbody>(), targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                foreach (Collider collider in sourceBones[i].GetComponents<Collider>())
                    CopyIfPresent(collider, targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyJoint(sourceBones[i], targetBones[i], sourceBones, targetBones);

            PrefabUtility.SaveAsPrefabAsset(prefab, path);
            Debug.Log(
                $"[래그돌 복제] {path} — 시체 리그의 뼈 {sourceBones.Count}개를 살아있는 리그로 "
                    + "복제했다. 이어서 Tools > Ragdoll > Finish Setup - Player를 실행할 것"
            );
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefab);
        }
    }

    private static bool CloneInto(
        Transform sourceBoneRoot,
        List<Transform> sourceBones,
        string targetPath
    )
    {
        GameObject target = PrefabUtility.LoadPrefabContents(targetPath);
        if (target == null)
        {
            Debug.LogError($"[래그돌 복제] 프리팹을 열 수 없다: {targetPath}");
            return false;
        }

        try
        {
            Transform targetBoneRoot = ResolveBoneRoot(target, targetPath);
            if (targetBoneRoot == null)
                return false;

            if (!MapBones(sourceBoneRoot, sourceBones, targetBoneRoot, targetPath,
                    out List<Transform> targetBones))
                return false;

            StripExistingRagdoll(targetBones);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyIfPresent(sourceBones[i].GetComponent<Rigidbody>(), targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                foreach (Collider collider in sourceBones[i].GetComponents<Collider>())
                    CopyIfPresent(collider, targetBones[i]);

            for (int i = 0; i < sourceBones.Count; i++)
                CopyJoint(sourceBones[i], targetBones[i], sourceBones, targetBones);

            string ragdollNote = EnsureNpcRagdoll(target);

            PrefabUtility.SaveAsPrefabAsset(target, targetPath);
            Debug.Log(
                $"[래그돌 복제] {targetPath} — 뼈 {sourceBones.Count}개 복제 완료 "
                    + $"({RagdollSetup.k_npcCitizenPrefab} 기준). {ragdollNote}"
            );
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(target);
        }
    }

    /// <summary>원본 뼈마다 대상 뼈를 찾아 좌표를 대조한다. 하나라도 어긋나면 중단한다.</summary>
    private static bool MapBones(
        Transform sourceBoneRoot,
        List<Transform> sourceBones,
        Transform targetBoneRoot,
        string targetPath,
        out List<Transform> targetBones
    )
    {
        targetBones = new List<Transform>(sourceBones.Count);
        List<string> problems = new List<string>();

        for (int i = 0; i < sourceBones.Count; i++)
        {
            string relative = RelativePath(sourceBoneRoot, sourceBones[i]);
            Transform match = targetBoneRoot.Find(relative);
            if (match == null)
            {
                problems.Add($"  {relative} — 대상에 없다");
                targetBones.Add(null);
                continue;
            }

            Vector3 expected = sourceBoneRoot.InverseTransformPoint(sourceBones[i].position);
            Vector3 actual = targetBoneRoot.InverseTransformPoint(match.position);
            float gap = Vector3.Distance(expected, actual);
            if (gap > k_boneMatchTolerance)
                problems.Add($"  {relative} — {gap * 100f:F2}cm 어긋남");

            targetBones.Add(match);
        }

        if (problems.Count == 0)
            return true;

        Debug.LogError(
            $"[래그돌 복제] {targetPath} — 뼈대가 원본과 달라 <b>건너뛴다</b> (아무것도 고치지 않았다).\n"
                + string.Join("\n", problems)
                + "\n\n체형이 다른 캐릭터다 — 이 프리팹은 내장 Ragdoll Wizard로 리그를 직접 만들고 "
                + "Tools > Ragdoll > Finish Setup - NPC (전체)를 돌릴 것."
        );
        return false;
    }

    private static void StripExistingRagdoll(List<Transform> targetBones)
    {
        for (int i = 0; i < targetBones.Count; i++)
            foreach (CharacterJoint joint in targetBones[i].GetComponents<CharacterJoint>())
                Object.DestroyImmediate(joint, true);

        for (int i = 0; i < targetBones.Count; i++)
        {
            foreach (NetworkRigidbody netBody in targetBones[i].GetComponents<NetworkRigidbody>())
                Object.DestroyImmediate(netBody, true);
            foreach (NetworkTransform netTransform in targetBones[i].GetComponents<NetworkTransform>())
                Object.DestroyImmediate(netTransform, true);
        }

        for (int i = 0; i < targetBones.Count; i++)
        {
            foreach (Collider collider in targetBones[i].GetComponents<Collider>())
                Object.DestroyImmediate(collider, true);
            foreach (Rigidbody body in targetBones[i].GetComponents<Rigidbody>())
                Object.DestroyImmediate(body, true);
        }
    }

    private static void CopyIfPresent(Component source, Transform target)
    {
        if (source == null)
            return;

        ComponentUtility.CopyComponent(source);
        ComponentUtility.PasteComponentAsNew(target.gameObject);
    }

    /// <summary>관절을 복제하고 connectedBody를 대상 프리팹 안의 뼈로 다시 연결한다.</summary>
    private static void CopyJoint(
        Transform sourceBone,
        Transform targetBone,
        List<Transform> sourceBones,
        List<Transform> targetBones
    )
    {
        CharacterJoint sourceJoint = sourceBone.GetComponent<CharacterJoint>();
        if (sourceJoint == null)
            return;

        Rigidbody sourceConnected = sourceJoint.connectedBody;
        int index = sourceConnected == null
            ? -1
            : sourceBones.IndexOf(sourceConnected.transform);

        sourceJoint.connectedBody = null;
        CopyIfPresent(sourceJoint, targetBone);
        sourceJoint.connectedBody = sourceConnected;

        CharacterJoint targetJoint = targetBone.GetComponent<CharacterJoint>();
        if (targetJoint == null)
            return;

        targetJoint.connectedBody =
            index >= 0 ? targetBones[index].GetComponent<Rigidbody>() : null;

        if (index < 0)
        {
            Debug.LogWarning(
                $"[래그돌 복제] {sourceBone.name}의 관절이 리그 밖 Rigidbody를 가리킨다 — "
                    + "연결을 비워 뒀다. 원본 리그를 확인할 것"
            );
        }
    }

    /// <summary>프리팹 루트에 NpcRagdoll을 보장한다(멱등).</summary>
    private static string EnsureNpcRagdoll(GameObject target)
    {
        if (target.GetComponent<NpcRagdoll>() != null)
            return "NpcRagdoll — 이미 있음";

        target.AddComponent<NpcRagdoll>();
        return "NpcRagdoll — 새로 붙임";
    }

    private static Transform ResolveBoneRoot(GameObject root, string prefabPath)
    {
        Transform rigOwner = root.transform.Find(RagdollSetup.k_npcRigOwnerPath);
        if (rigOwner == null)
        {
            Debug.LogError(
                $"[래그돌 복제] 리그 소유자 '{RagdollSetup.k_npcRigOwnerPath}'를 {prefabPath}에서 "
                    + "찾지 못했다 — 프리팹 구조가 바뀌었으면 RagdollSetup.k_npcRigOwnerPath를 맞출 것"
            );
            return null;
        }

        Transform boneRoot = rigOwner.Find(RagdollRig.k_defaultBoneRootName);
        if (boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌 복제] 몸통 리그 '{RagdollRig.k_defaultBoneRootName}'를 "
                    + $"{prefabPath}의 '{RagdollSetup.k_npcRigOwnerPath}' 직속 자식에서 찾지 못했다"
            );
        }

        return boneRoot;
    }

    private static List<Transform> CollectRagdollBones(GameObject root, Transform boneRoot)
    {
        HashSet<Transform> set = new HashSet<Transform>();
        foreach (CharacterJoint joint in root.GetComponentsInChildren<CharacterJoint>(true))
        {
            if (joint.GetComponent<Rigidbody>() != null)
                set.Add(joint.transform);
            if (joint.connectedBody != null)
                set.Add(joint.connectedBody.transform);
        }

        List<Transform> bones = new List<Transform>();
        foreach (Transform bone in set)
        {
            if (bone.IsChildOf(boneRoot))
                bones.Add(bone);
        }

        bones.Sort(
            (a, b) => RelativePath(boneRoot, a).CompareTo(RelativePath(boneRoot, b))
        );
        return bones;
    }

    private static string RelativePath(Transform root, Transform target)
    {
        StringBuilder path = new StringBuilder(target.name);
        Transform cursor = target;
        while (cursor.parent != null && cursor.parent != root)
        {
            cursor = cursor.parent;
            path.Insert(0, cursor.name + "/");
        }

        return path.ToString();
    }
}
