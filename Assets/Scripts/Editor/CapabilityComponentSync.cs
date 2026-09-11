using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 능력 컴포넌트(<see cref="ChannelGauge"/> 등)를 프리팹 자산에 반영·점검한다.
///
/// <b>왜 필요한가.</b> 능력 선택의 단일 출처는 소비자 클래스의 <c>[RequireComponent]</c> 선언이지만,
/// Unity의 자동 보정은 <b>에디터 인메모리 동작이라 프리팹 자산에 저장되지 않는다</b> — 콘솔에
/// "Creating missing ... component"가 찍혀도 .prefab 파일은 그대로다. 저장하지 않은 채 빌드하면
/// 런타임에 컴포넌트가 없다. 이 스크립트가 그 선언을 실제 자산에 밀어 넣는다.
///
/// <b>판정은 반드시 직렬화된 YAML로 한다.</b> <see cref="PrefabUtility.LoadPrefabContents"/>가
/// 돌려주는 사본에는 자동 보정이 이미 적용돼 있어, <c>GetComponent</c>로 물으면 누락이 항상
/// "없음"으로 나온다 — 로드된 오브젝트로 점검하면 눈을 감는다. 그래서 .prefab 텍스트에 스크립트
/// guid가 실제로 있는지를 본다.
///
/// 런타임 AddComponent는 절대 금지다 — NGO는 스폰 시점의 NetworkBehaviour 인덱스로 RPC를
/// 라우팅하므로 실행 중 추가하면 피어 간 인덱스가 어긋난다. 그래서 에디터에서만 반영한다.
/// 상속에서 합성으로 옮긴 경위는 docs/channeled-interaction-split.md 참고.
/// </summary>
public static class CapabilityComponentSync
{
    private const string k_menuAudit = "Tools/능력 컴포넌트/프리팹 점검";
    private const string k_menuApply = "Tools/능력 컴포넌트/프리팹 반영";
    private const string k_prefabFolder = "Assets/Prefabs";

    // 이 스크립트가 붙여도 되는 타입 — 무관한 RequireComponent(Rigidbody 등)까지 임의로 추가하지 않기
    // 위한 화이트리스트다. 단계가 진행되면 ToastFeedback·OwnerFeedback을 여기 추가한다.
    private static readonly Type[] s_capabilities = { typeof(ChannelGauge) };

    [MenuItem(k_menuAudit)]
    public static void Audit() => Sync(apply: false);

    [MenuItem(k_menuApply)]
    public static void Apply() => Sync(apply: true);

    private static void Sync(bool apply)
    {
        List<string> found = new();
        List<string> problems = new();

        foreach (string prefabGuid in AssetDatabase.FindAssets("t:Prefab", new[] { k_prefabFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuid);

            // 자동 보정이 닿지 않는 유일한 정본 — 디스크에 직렬화된 내용
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

                // 로드 사본에는 자동 보정으로 컴포넌트가 이미 올라와 있으므로, 저장만 하면 반영된다
                if (apply && missing)
                    PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                // 실패로 빠져나가도 임시 씬이 남지 않게 한다 (LoadPrefabContents는 숨은 씬을 만든다)
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        Report(apply, found, problems);
    }

    // 이 오브젝트가 선언한 능력이 직렬화 텍스트에 실제로 들어 있는지 본다.
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
                continue; // 자동 보정이 돌았다면 여기 오지 않는다

            string scriptGuid = ScriptGuidOf(component);
            if (string.IsNullOrEmpty(scriptGuid))
            {
                problems.Add($"{type.Name}의 스크립트 guid를 찾을 수 없다: {path}");
                continue;
            }

            if (serialized.Contains(scriptGuid))
                continue; // 이미 자산에 있다

            found.Add($"{type.Name} → {path} ({go.name})");
            missing = true;
        }
        return missing;
    }

    // 이 오브젝트의 컴포넌트들이 [RequireComponent]로 요구하는 능력 타입.
    private static HashSet<Type> DeclaredCapabilities(GameObject go)
    {
        HashSet<Type> required = new();
        foreach (MonoBehaviour behaviour in go.GetComponents<MonoBehaviour>())
        {
            if (behaviour == null)
                continue; // 스크립트가 깨진 컴포넌트 — 여기서 판단할 수 없다

            // inherit: true — HomeRunBaton·ToyHammer처럼 상속으로 물려받은 선언까지 본다
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

    // 이름 검색이 아니라 인스턴스에서 역추적한다 — 동명 타입에 걸리지 않는다.
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
            Debug.LogWarning($"[능력 컴포넌트] 누락 {found.Count}건 — '프리팹 반영'을 실행할 것:\n  {body}");
    }
}
