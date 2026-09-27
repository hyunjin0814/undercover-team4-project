using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Shop 씬 매니저 — 라운드 사이 준비 허브. 장비 회수·지급을 하고 호스트 출동으로 게임 씬에 넘어간다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ShopManager : SceneManagerBase
{
    private const float k_localReadyTimeoutSeconds = 10f;

    private SessionManager Session => App.Net.Session;
    private bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool m_dispatched;

    /// <summary>내 플레이어가 스폰 지점으로 재배치될 때까지 로딩 화면을 유지한다.</summary>
    public override UniTask WaitUntilReadyAsync(CancellationToken token) =>
        WaitUntilLocallyReadyAsync(token, IsLocallyReady, k_localReadyTimeoutSeconds, "플레이어 재배치");

    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || !net.IsListening)
            return true;

        NetworkClient local = net.LocalClient;
        if (local == null)
            return true;

        NetworkObject player = local.PlayerObject;
        if (player == null)
            return false;

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        return movement == null || movement.IsRepositionApplied;
    }

    public bool IsDispatched => m_dispatched;

    private void Start()
    {
        if (!IsServer)
            return;

        Session?.SetLockedAsync(false).Forget();

        DespawnDroppedItems();
        App.Game.ArrestJudge?.ServerResetRound();
        App.Game.WrongfulArrestPenalty?.ServerResetRound();

        NetworkManager.Singleton.SceneManager.OnLoadComplete += HandleLoadComplete;
        ResetPlayer(NetworkManager.Singleton.LocalClientId);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (NetworkManager.Singleton?.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadComplete -= HandleLoadComplete;
    }

    private void HandleLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        if (sceneName != gameObject.scene.name)
            return;
        if (clientId == NetworkManager.Singleton.LocalClientId)
            return;
        ResetPlayer(clientId);
    }

    private void ResetPlayer(ulong clientId)
    {
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            return;
        client.PlayerObject?.GetComponent<PlayerHealth>()?.ServerResetState();
        client.PlayerObject?.GetComponent<PlayerItemSupply>()?.ServerClearHeldItems();
        client.PlayerObject?.GetComponent<PlayerWallet>()?.ServerResetRound();
        client.PlayerObject?.GetComponent<PlayerKillCredit>()?.ServerResetRound();
        client.PlayerObject?.GetComponent<PlayerAssistCredit>()?.ServerResetRound();
    }

    private static void DespawnDroppedItems()
    {
        NetworkSpawnManager spawnManager = NetworkManager.Singleton.SpawnManager;
        if (spawnManager == null)
            return;

        int despawned = 0;
        foreach (NetworkObject spawned in new List<NetworkObject>(spawnManager.SpawnedObjectsList))
        {
            if (spawned == null || spawned.transform.parent != null || !spawned.TryGetComponent(out ItemBase _))
                continue;

            spawned.Despawn(true);
            despawned++;
        }

        Debug.Log($"[ShopManager] 바닥 아이템 회수 — {despawned}개");
    }

    /// <summary>호스트 전용 — 출동. 게임 씬으로 전환하며 세션을 잠근다(게임 진행 중 신규 접속 차단).</summary>
    public void Dispatch()
    {
        if (!IsServer || m_dispatched)
            return;
        m_dispatched = true;

        SaveService.SaveAsync().Forget();

        Session?.SetLockedAsync(true).Forget();
        MoveToNextScene(EScene.Game);
    }
}
