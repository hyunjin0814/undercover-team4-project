using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Localization.Components;

/// <summary>
/// SessionCanvas 버튼 재배선으로 지워진 LocalizeStringEvent 키 11개를 복원한다.
/// </summary>
public static class SessionCanvasLocalizationKeyFixer
{
    private const string k_menu = "Tools/Localization/SessionCanvas 버튼 키 복원";
    private const string k_prefabPath = "Assets/Prefabs/UI/SessionCanvas.prefab";

    private static readonly (string path, string key)[] k_targets =
    {
        ("AuthGate/Window/Contents/Buttons/SignUpBtn/Text (TMP)", "Title.Gate.SignUp"),
        ("AuthGate/Window/Contents/Buttons/SignInBtn/Text (TMP)", "Title.Gate.SignIn"),
        ("AuthGate/Window/Contents/Buttons/GuestLoginBtn/Text (TMP)", "Title.Gate.Guest"),
        ("AuthGate/Window/Title", "Title.Gate.Title"),
        ("SessionPanel/Buttons/QuitBtn/Text (TMP)", "Title.Button.Quit"),
        ("SessionPanel/Buttons/SettingsBtn/Text (TMP)", "Title.Button.Settings"),
        ("SessionPanel/Window/Contents/Buttons/JoinBtn/Text (TMP)", "Title.Session.JoinButton"),
        ("SessionPanel/Window/Contents/Buttons/CreateBtn/Text (TMP)", "Title.Session.CreateButton"),
        ("SessionPanel/Buttons/TutorialBtn/Text (TMP)", "Title.Tutorial.Button"),
        ("SessionPanel/Buttons/SignOutBtn/Text (TMP)", "Title.Auth.SignOut"),
        ("SessionPanel/Window/Contents/Nickname/NicknameRow/ApplyNicknameBtn/Text (TMP)", "Title.Auth.ApplyNickname"),
        ("SessionPanel/Window/Contents/Buttons/ContinueBtn/Text (TMP)", "Title.Session.ContinueButton"),
    };

    [MenuItem(k_menu)]
    public static void Fix()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(k_prefabPath);
        try
        {
            int fixedCount = 0;
            var missing = new List<string>();

            foreach ((string path, string key) in k_targets)
            {
                Transform target = root.transform.Find(path);
                if (target == null)
                {
                    missing.Add($"경로를 찾을 수 없음: {path}");
                    continue;
                }

                var evt = target.GetComponent<LocalizeStringEvent>();
                if (evt == null)
                {
                    missing.Add($"LocalizeStringEvent 컴포넌트 없음: {path}");
                    continue;
                }

                evt.StringReference.TableEntryReference = key;
                EditorUtility.SetDirty(evt);
                fixedCount++;
            }

            if (fixedCount > 0)
                PrefabUtility.SaveAsPrefabAsset(root, k_prefabPath);

            Debug.Log($"[SessionCanvas 키 복원] {fixedCount}/{k_targets.Length}개 복원됨");
            foreach (string m in missing)
                Debug.LogWarning($"[SessionCanvas 키 복원] {m}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
