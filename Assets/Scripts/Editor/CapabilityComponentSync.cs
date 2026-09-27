using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 소비자의 [RequireComponent] 선언대로 능력 컴포넌트를 프리팹 자산에 실제로 추가·점검한다.
/// 누락 판정은 로드된 오브젝트가 아니라.prefab YAML의 스크립트 guid로 한다.
/// </summary>
public static class CapabilityComponentSync
{
    private const string k_menuAudit = "Tools/능력 컴포넌트/프리팹 점검";
    private const string k_menuApply = "Tools/능력 컴포넌트/프리팹 반영";
    private const string k_prefabFolder = "Assets/Prefabs";

    private static readonly Type[] s_capabilities =
    {
        typeof(ChannelGauge),
        typeof(ToastFeedback),
        typeof(OwnerFeedback),
    };

    [MenuItem(k_menuAudit)]
    public static void Audit() => Sync(apply: false);

    [MenuItem(k_menuApply)]
    public static void Apply() => Sync(apply: true);

    private static void Sync(bool apply)
    {
        List<string> found = new();
        List<string> problems = new();

        foreach (
            string prefabGuid in AssetDatabase.FindAssets("t:Prefab", new[] { k_prefabFolder })
        )
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);

            string serialized = File.ReadAllText(path);

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                problems.Add($"프리팹을 열 수 없다: {path}");
                continue;
            }

            try
            {
                bool missing = false;
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    missing |= Inspect(child.gameObject, path, serialized, found, problems);

                if (apply && missing)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        Report(apply, found, problems);
    }

    private static bool Inspect(
        GameObject go,
        string path,
        string serialized,
        List<string> found,
        List<string> problems
    )
    {
        bool missing = false;
        foreach (Type type in DeclaredCapabilities(go))
        {
            Component component = go.GetComponent(type);
            if (component == null)
                continue;

            string scriptGuid = ScriptGuidOf(component);
            if (string.IsNullOrEmpty(scriptGuid))
            {
                problems.Add($"{type.Name}의 스크립트 guid를 찾을 수 없다: {path}");
                continue;
            }

            if (serialized.Contains(scriptGuid))
                continue;

            found.Add($"{type.Name} → {path} ({go.name})");
            missing = true;
        }
        return missing;
    }

    private static HashSet<Type> DeclaredCapabilities(GameObject go)
    {
        HashSet<Type> required = new();
        foreach (MonoBehaviour behaviour in go.GetComponents<MonoBehaviour>())
        {
            if (behaviour == null)
                continue;

            object[] attributes = behaviour
                .GetType()
                .GetCustomAttributes(typeof(RequireComponent), inherit: true);

            foreach (object attribute in attributes)
            {
                RequireComponent require = (RequireComponent)attribute;
                Collect(require.m_Type0, required);
                Collect(require.m_Type1, required);
                Collect(require.m_Type2, required);
            }
        }
        return required;
    }

    private static void Collect(Type type, HashSet<Type> into)
    {
        if (type != null && Array.IndexOf(s_capabilities, type) >= 0)
            into.Add(type);
    }

    private static string ScriptGuidOf(Component component)
    {
        if (component is not MonoBehaviour behaviour)
            return null;

        MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
        if (script == null)
            return null;

        return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
    }

    private static void Report(bool apply, List<string> found, List<string> problems)
    {
        foreach (string problem in problems)
            Debug.LogError($"[능력 컴포넌트] {problem}");

        if (found.Count == 0)
        {
            Debug.Log("[능력 컴포넌트] 누락 없음 — 모든 프리팹이 선언과 일치한다");
            return;
        }

        string body = string.Join("\n  ", found);
        if (apply)
            Debug.Log($"[능력 컴포넌트] {found.Count}건 추가·저장했다:\n  {body}");
        else
            Debug.LogWarning(
                $"[능력 컴포넌트] 누락 {found.Count}건 — '프리팹 반영'을 실행할 것:\n  {body}"
            );
    }
}
