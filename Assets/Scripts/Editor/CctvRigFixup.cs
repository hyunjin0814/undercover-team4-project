using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// CCTV 설치물의 조준 회전을 루트에서 Head로 옮긴다(보이는 결과는 유지). 루트가 이미 identity면 건너뛴다.
/// </summary>
public static class CctvRigFixup
{
    private const string k_menu = "Tools/CCTV/루트 회전을 Head로 옮기기";
    private const float k_identityDot = 0.99999f;

    [MenuItem(k_menu)]
    public static void MoveRootRotationToHead()
    {
        CCTVNode[] nodes = Object.FindObjectsByType<CCTVNode>(FindObjectsSortMode.None);
        if (nodes.Length == 0)
        {
            Debug.LogError("[CCTV 리그] 씬에 CCTVNode가 없다");
            return;
        }

        int moved = 0;
        int skipped = 0;
        var problems = new List<string>();
        var report = new StringBuilder("[CCTV 리그] 루트 회전 이관\n");

        Undo.SetCurrentGroupName("CCTV 루트 회전을 Head로");
        int group = Undo.GetCurrentGroup();

        foreach (CCTVNode node in nodes)
        {
            Transform root = node.transform;
            Quaternion rotation = root.localRotation;

            if (Mathf.Abs(Quaternion.Dot(rotation, Quaternion.identity)) > k_identityDot)
            {
                skipped++;
                continue;
            }

            if (!TryFindParts(root, out Transform head, out Transform body))
            {
                problems.Add(node.name);
                continue;
            }

            Undo.RecordObject(root, k_menu);
            Undo.RecordObject(head, k_menu);
            if (body != null)
                Undo.RecordObject(body, k_menu);

            head.localRotation = rotation * head.localRotation;

            if (body != null)
            {
                body.localPosition = rotation * body.localPosition;
                body.localRotation = rotation * body.localRotation;
            }

            root.localRotation = Quaternion.identity;

            moved++;
            report.AppendLine($"  {node.name,-24} Head ← {rotation.eulerAngles}");
        }

        Undo.CollapseUndoOperations(group);

        report.AppendLine($"  → 이관 {moved}건, 건너뜀 {skipped}건. 씬을 저장할 것");
        Debug.Log(report.ToString());

        if (problems.Count > 0)
            Debug.LogWarning("[CCTV 리그] 카메라를 품은 자식을 못 찾아 건너뜀: " + string.Join(", ", problems));
    }

    private static bool TryFindParts(Transform root, out Transform head, out Transform body)
    {
        head = null;
        body = null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (head == null && child.GetComponentInChildren<Camera>(true) != null)
                head = child;
            else if (body == null)
                body = child;
        }

        return head != null;
    }
}
