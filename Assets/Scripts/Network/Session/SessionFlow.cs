using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 온라인 계층(세션·NGO·음성)을 정해진 순서로 종료하는 정적 진입점.
/// </summary>
public static class SessionFlow
{
    private const float k_shutdownTimeoutSeconds = 5f;

    private static bool s_busy;

    public static bool IsBusy => s_busy;

    /// <summary>Vivox 로그아웃 → 세션 이탈 → 타이틀 복귀 순으로 메인으로 나간다. 인증은 유지한다.</summary>
    public static async UniTask LeaveToMainAsync()
    {
        if (s_busy)
            return;
        s_busy = true;

        try
        {
            if (App.Net.Vivox != null)
                await App.Net.Vivox.LogoutAsync();

            if (App.Net.Session != null)
                await App.Net.Session.LeaveAsync();

            if (
                App.Net.Session?.CurrentSession == null
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsListening
            )
                NetworkManager.Singleton.Shutdown();

            await WaitForNetworkShutdownAsync();

            if (App.CurrentScene != EScene.Title)
                App.LoadScene(EScene.Title);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SessionFlow] 종료 중 오류: {ex}");
        }
        finally
        {
            s_busy = false;
        }
    }

    public static async UniTask WaitForNetworkShutdownAsync()
    {
        float deadline = Time.realtimeSinceStartup + k_shutdownTimeoutSeconds;
        while (
            NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && Time.realtimeSinceStartup < deadline
        )
        {
            await UniTask.Yield();
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            Debug.LogWarning(
                "[SessionFlow] NGO가 제한시간 내에 완전히 내려가지 않음 — 그대로 진행"
            );
    }
}
