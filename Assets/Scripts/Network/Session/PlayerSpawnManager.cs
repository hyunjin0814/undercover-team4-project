using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 씬별 플레이어 스폰·재배치 정책과 게임 씬 진입 시 기본 장비 지급을 담당한다.
/// 로비·타이틀은 접속만 승인하고, 상점·게임 씬은 없으면 스폰하고 있으면 재배치한다.
/// </summary>
public class PlayerSpawnManager : MonoBehaviour
{
    [Tooltip("이 씬에서 플레이어를 스폰/유지하는가 (로비·타이틀은 false — UI 대기)")]
    [SerializeField] private bool m_spawnPlayers = true;

    [SerializeField] private Transform m_spawnPoint;
    [SerializeField] private float m_spreadRadius = 1.5f;

    private NetworkManager m_networkManager;
    private int m_placedCount;
    private bool m_ownsCallback;

    private void Start()
    {
        m_networkManager = NetworkManager.Singleton;
        if (m_networkManager == null)
        {
            Debug.LogWarning("[PlayerSpawnManager] NetworkManager 없음.");
            return;
        }

        if (App.Net.Session != null)
        {
            App.Net.Session.Approval.SpawnPolicy = ConfigureSpawn;
        }
        else
        {
            m_ownsCallback = true;
            m_networkManager.ConnectionApprovalCallback = OnConnectionApproval;
        }

        if (!m_networkManager.IsServer)
            return;

        if (m_spawnPlayers)
        {
            m_networkManager.SceneManager.OnLoadComplete += HandleLoadComplete;
            EnsureAndPlace(m_networkManager.LocalClientId);
        }
        else
        {
            DespawnAllPlayers();
            DespawnLooseItems();
        }
    }

    private void OnDestroy()
    {
        if (m_ownsCallback && m_networkManager != null)
            m_networkManager.ConnectionApprovalCallback = null;
        else if (App.Net.Session != null && App.Net.Session.Approval.SpawnPolicy == (System.Action<NetworkManager.ConnectionApprovalResponse>)ConfigureSpawn)
            App.Net.Session.Approval.SpawnPolicy = null;

        if (m_networkManager != null && m_networkManager.SceneManager != null)
            m_networkManager.SceneManager.OnLoadComplete -= HandleLoadComplete;
    }

    /// <summary>플레이어를 두지 않는 씬(로비·타이틀) 전용 — 남은 플레이어를 전부 내린다.</summary>
    private void DespawnAllPlayers()
    {
        var players = new List<NetworkObject>();
        foreach (NetworkClient client in m_networkManager.ConnectedClientsList)
        {
            if (client.PlayerObject != null)
                players.Add(client.PlayerObject);
        }

        foreach (NetworkObject player in players)
        {
            if (player != null && player.IsSpawned)
                player.Despawn(destroy: true);
        }

        if (players.Count > 0)
            Debug.Log($"[PlayerSpawnManager] 플레이어 {players.Count}개 정리 — 이 씬은 플레이어를 두지 않는다");
    }

    /// <summary>플레이어를 두지 않는 씬 전용 — 바닥에 버려진 아이템(소지품 정리분 제외)을 내린다.</summary>
    private void DespawnLooseItems()
    {
        var items = new List<NetworkObject>();
        foreach (NetworkObject spawned in m_networkManager.SpawnManager.SpawnedObjectsList)
        {
            if (spawned != null && spawned.GetComponent<ItemBase>() != null)
                items.Add(spawned);
        }

        foreach (NetworkObject item in items)
        {
            if (item != null && item.IsSpawned)
                item.Despawn(destroy: true);
        }

        if (items.Count > 0)
            Debug.Log($"[PlayerSpawnManager] 남은 아이템 {items.Count}개 정리 — 이 씬은 아이템을 두지 않는다");
    }

    private void HandleLoadComplete(ulong clientId, string sceneName, LoadSceneMode loadSceneMode)
    {
        if (sceneName != gameObject.scene.name) return;
        if (clientId == m_networkManager.LocalClientId) return;
        EnsureAndPlace(clientId);
    }

    private void EnsureAndPlace(ulong clientId)
    {
        if (!m_networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
            return;

        if (client.PlayerObject == null)
            SpawnPlayerFor(clientId);
        else
            RepositionPlayer(client.PlayerObject);

        PlayerItemSupply supply = client.PlayerObject?.GetComponent<PlayerItemSupply>();
        if (supply == null)
            return;

        if (App.CurrentScene == EScene.Game)
        {
            supply.ServerClearHeldItems();
            supply.ServerGrantStartingGear();
        }
        else if (App.CurrentScene == EScene.Shop)
        {
            supply.ServerGrantShopGear();
        }
    }

    private void SpawnPlayerFor(ulong clientId)
    {
        GameObject prefab = m_networkManager.NetworkConfig.PlayerPrefab;
        if (prefab == null)
        {
            Debug.LogError("[PlayerSpawnManager] NetworkConfig.PlayerPrefab 미설정");
            return;
        }

        (Vector3 pos, Quaternion rot) = NextPose();
        NetworkObject no = Instantiate(prefab, pos, rot).GetComponent<NetworkObject>();
        no.SpawnAsPlayerObject(clientId, destroyWithScene: false);
        Debug.Log($"[PlayerSpawnManager] 클라이언트 {clientId} 플레이어 스폰: {pos}");
    }

    private void RepositionPlayer(NetworkObject playerObject)
    {
        PlayerMovement movement = playerObject.GetComponent<PlayerMovement>();
        if (movement == null) return;
        (Vector3 pos, Quaternion rot) = NextPose();
        movement.ServerReposition(pos, rot);
    }

    /// <summary>스폰된 플레이어를 스폰 지점으로 순간이동시킨다(맵 이탈 복귀용). 서버 전용.</summary>
    public void ServerReturnToSpawn(PlayerMovement movement)
    {
        if (movement == null) return;
        (Vector3 pos, Quaternion rot) = NextPose();
        movement.ServerTeleport(pos, rot);
    }

    private (Vector3, Quaternion) NextPose()
    {
        Vector3 basePos = m_spawnPoint != null ? m_spawnPoint.position : Vector3.zero;
        Quaternion rot = m_spawnPoint != null ? m_spawnPoint.rotation : Quaternion.identity;
        return (basePos + GetSpreadOffset(m_placedCount++), rot);
    }

    private void ConfigureSpawn(NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = m_spawnPlayers;
        if (m_spawnPlayers && m_spawnPoint != null)
            (response.Position, response.Rotation) = NextPose();
    }

    private void OnConnectionApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        ConfigureSpawn(response);
    }

    private Vector3 GetSpreadOffset(int index)
    {
        if (index == 0) return Vector3.zero;
        float angle = (index % 6) * 60f * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * m_spreadRadius;
    }
}
