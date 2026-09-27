using System;
using Cysharp.Threading.Tasks;
using Unity.Services.Core;
using UnityEngine;

/// <summary>
/// AuthBootstrap 수동 조작용 OnGUI 디버그 패널 — 테스트 씬에만 붙인다.
/// </summary>
public class AuthDebugGui : MonoBehaviour
{
    [SerializeField]
    private float m_guiTopOffset = 10f;

    private bool m_isBusy;
    private string m_status = "대기 중...";
    private string m_nicknameInput = string.Empty;

    private void OnGUI()
    {
        AuthBootstrap auth = App.Net.Auth;
        if (auth == null || auth.IsNetworkConnected)
            return;

        GUILayout.BeginArea(new Rect(700, m_guiTopOffset, 380, 360));

        GUILayout.Label("Authentication (익명) — 상태");

        bool initialized = UnityServices.State == ServicesInitializationState.Initialized;
        GUILayout.Label($"초기화됨: {initialized}");
        GUILayout.Label($"IsSignedIn: {auth.IsSignedIn}");
        GUILayout.Label(
            $"PlayerId: {(string.IsNullOrEmpty(auth.PlayerId) ? "(없음)" : auth.PlayerId)}"
        );
        GUILayout.Label(
            $"Nickname: {(string.IsNullOrEmpty(auth.Nickname) ? "(없음)" : auth.Nickname)}"
        );
        GUILayout.Label($"PlayerName(전체): {auth.PlayerName}");

        GUILayout.BeginHorizontal();
        m_nicknameInput = GUILayout.TextField(m_nicknameInput, 128);
        GUI.enabled = !m_isBusy && auth.IsSignedIn;
        if (GUILayout.Button("적용", GUILayout.Width(60)))
            ApplyNicknameAsync(auth, m_nicknameInput).Forget();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(
            $"SessionTokenExists: {(initialized ? auth.SessionTokenExists.ToString() : "(미초기화)")}"
        );

        GUILayout.Label($"연결됨(세션/NGO): {auth.IsNetworkConnected}");
        GUILayout.Space(8);

        bool canSignOut = !m_isBusy && (auth.CanSignOut == null || auth.CanSignOut());

        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("Sign In (init + 익명 로그인)"))
        {
            SignInAsync(auth).Forget();
        }

        GUI.enabled = canSignOut;
        if (GUILayout.Button("Sign Out"))
        {
            auth.SignOut();
        }

        if (GUILayout.Button("New Player (로그아웃 + 토큰 삭제 → 새 PlayerId)"))
        {
            auth.ClearSessionToken();
        }

        GUI.enabled = true;

        GUILayout.Space(8);
        GUILayout.Label(m_status);

        GUILayout.EndArea();
    }

    private async UniTaskVoid SignInAsync(AuthBootstrap auth)
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        m_status = "초기화 + 익명 로그인 중...";

        try
        {
            await auth.InitializeAndSignInAsync(auth.Profile);
            m_status = $"로그인 성공 - PlayerId: {auth.PlayerId}";
        }
        catch (Exception ex)
        {
            m_status = $"로그인 실패 - {ex.Message}";
        }
        finally
        {
            m_isBusy = false;
        }
    }

    private async UniTaskVoid ApplyNicknameAsync(AuthBootstrap auth, string name)
    {
        if (m_isBusy)
            return;

        m_isBusy = true;
        try
        {
            await auth.SetPlayerNameAsync(name);
            m_status = $"닉네임 적용: {auth.PlayerName}";
        }
        catch (Exception ex)
        {
            m_status = $"닉네임 실패 - {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            m_isBusy = false;
        }
    }
}
