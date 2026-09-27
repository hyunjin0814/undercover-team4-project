using System.Collections.Generic;
using Ricimi;
using UnityEditor;
using UnityEngine;

/// <summary>
/// SessionCanvas 버튼 중 CleanButtonConfig가 빠진 것에 기본값으로 컴포넌트를 복원한다.
/// </summary>
public static class SessionCanvasCleanButtonConfigFixer
{
    private const string k_menu = "Tools/UI/SessionCanvas CleanButtonConfig 복원";
    private const string k_prefabPath = "Assets/Prefabs/UI/SessionCanvas.prefab";

    private const float k_fadeTime = 0.3f;
    private const float k_onHoverAlpha = 0.6f;
    private const float k_onClickAlpha = 0.4f;

    private static readonly string[] k_targets =
    {
        "AuthGate/Window/Contents/Buttons/SignUpBtn",
        "AuthGate/Window/Contents/Buttons/SignInBtn",
        "AuthGate/Window/Contents/Buttons/GuestLoginBtn",
    };

    [MenuItem(k_menu)]
    public static void Fix()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(k_prefabPath);
        try
        {
            int fixedCount = 0;
            var missing = new List<string>();

            foreach (string path in k_targets)
            {
                Transform target = root.transform.Find(path);
                if (target == null)
                {
                    missing.Add($"경로를 찾을 수 없음: {path}");
                    continue;
                }

                if (target.GetComponent<CleanButtonConfig>() != null)
                {
                    missing.Add($"이미 CleanButtonConfig가 있음(건너뜀): {path}");
                    continue;
                }

                var config = target.gameObject.AddComponent<CleanButtonConfig>();
                config.fadeTime = k_fadeTime;
                config.onHoverAlpha = k_onHoverAlpha;
                config.onClickAlpha = k_onClickAlpha;
                EditorUtility.SetDirty(config);
                fixedCount++;
            }

            if (fixedCount > 0)
                PrefabUtility.SaveAsPrefabAsset(root, k_prefabPath);

            Debug.Log($"[SessionCanvas CleanButtonConfig 복원] {fixedCount}/{k_targets.Length}개 복원됨");
            foreach (string m in missing)
                Debug.LogWarning($"[SessionCanvas CleanButtonConfig 복원] {m}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
