using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 이벤트 NPC가 despawn될 때 ClientRpc로 각 피어에 소멸 잔상(NpcDespawnGhost)을 생성한다.
/// SuddenEventUtil.DespawnOrDestroy가 자동으로 재생한다.
/// </summary>
public class NpcDespawnVfx : NetworkBehaviour
{
    [Tooltip("몸체 잔상(고스트)에 씌울 머티리얼 — NPC의 현재 포즈를 베이크해 플리커시키며 사라지게 한다. 비우면 연출 없음")]
    [SerializeField] private Material m_ghostMaterial;

    /// <summary>despawn 직전 소멸 연출을 전 피어에 재생한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerPlay()
    {
        if (m_ghostMaterial == null)
            return;

        SpawnLocal();
        if (IsSpawned && IsServer)
            PlayClientRpc();
    }

    [ClientRpc]
    private void PlayClientRpc()
    {
        if (IsServer)
            return;
        SpawnLocal();
    }

    private void SpawnLocal()
    {
        NpcDespawnGhost.Spawn(transform, m_ghostMaterial);
    }
}
