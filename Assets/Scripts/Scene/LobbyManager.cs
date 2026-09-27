using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lobby 씬 매니저 — 세션 생성 후 최초 대기 공간이며, 호스트가 시작하면 상점으로 넘어간다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LobbyManager : SceneManagerBase
{
    private const float k_localReadyTimeoutSeconds = 10f;

    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_started;

    /// <summary>내 플레이어 오브젝트가 despawn될 때까지 로딩 화면을 유지한다.</summary>
    public override UniTask WaitUntilReadyAsync(CancellationToken token) =>
        WaitUntilLocallyReadyAsync(token, IsLocallyReady, k_localReadyTimeoutSeconds, "플레이어 정리");

    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || !net.IsListening)
            return true;

        return net.LocalClient == null || net.LocalClient.PlayerObject == null;
    }

    private void Start()
    {
        if (IsServer)
            Session?.SetLockedAsync(false).Forget();
    }

    /// <summary>호스트 전용 — 게임 시작. 상점(허브)으로 전환. 전 클라가 NGO 동기화로 따라온다.</summary>
    public void StartGame()
    {
        if (!IsServer || m_started) return;
        m_started = true;
        MoveToNextScene(EScene.Shop);
    }
}
