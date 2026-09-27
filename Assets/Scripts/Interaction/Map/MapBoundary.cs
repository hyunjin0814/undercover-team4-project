using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 경계 트리거 — 플레이 영역 밖으로 나간 플레이어를 본부 스폰 지점으로 되돌린다. 서버 권위.
/// </summary>
[RequireComponent(typeof(Collider))]
public class MapBoundary : MonoBehaviour
{
    [Tooltip("되돌릴 자리를 주는 스폰 매니저 — 같은 씬의 것을 배선한다")]
    [SerializeField] private PlayerSpawnManager m_spawnManager;

    private readonly HashSet<PlayerMovement> m_pending = new HashSet<PlayerMovement>();

    private Collider m_volume;

    private void Awake()
    {
        m_volume = GetComponent<Collider>();

        if (m_spawnManager == null)
        {
            Debug.LogError($"[맵 경계] 스폰 매니저 미배선 — {name}의 이탈 복귀가 꺼진다");
            enabled = false;
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerMovement movement = other.GetComponentInParent<PlayerMovement>();
        if (movement != null)
            m_pending.Add(movement);
    }

    private void Update()
    {
        if (!IsAuthority || m_pending.Count == 0)
            return;

        m_pending.RemoveWhere(TryReturn);
    }

    private bool TryReturn(PlayerMovement movement)
    {
        if (movement == null)
            return true;

        if (m_volume.bounds.Contains(movement.transform.position))
            return true;

        PlayerIncapacitation incapacitation = movement.GetComponent<PlayerIncapacitation>();
        if (incapacitation != null && incapacitation.IsIncapacitated)
            return false;

        m_spawnManager.ServerReturnToSpawn(movement);
        Debug.Log($"[맵 경계] 이탈 복귀: {movement.name}");
        return true;
    }

    private static bool IsAuthority =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
}
