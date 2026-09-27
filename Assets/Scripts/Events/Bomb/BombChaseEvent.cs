using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 추격 폭탄 돌발 이벤트 — 씬의 폭탄 상자 중 하나에서 BombDevice를 스폰하고 수명·정리만 맡는다(GDD 6-4).
/// 등장·추격·폭발은 스폰된 폭탄이 서버 권위로 처리하며, 라운드당 1개다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class BombChaseEvent : MonoBehaviour, ISuddenEvent
{
    [Header("폭탄 프리팹 (BombDevice)")]
    [SerializeField]
    private BombDevice m_bombPrefab;

    [Header("등장")]
    [Tooltip("상자에서 이 거리(m) 안의 NavMesh를 찾아 그 위에 올려놓는다 — 못 찾으면 다음 상자를 시도한다")]
    [SerializeField]
    private float m_navSampleMaxDistance = 5f;

    [Header("정리")]
    [Tooltip("폭발 후 폭탄 모델을 남겨 둘 시간(초). 0이면 터지는 순간 사라진다")]
    [SerializeField]
    private float m_resolvedLingerSeconds;

    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    public string DisplayName => "추격 폭탄";

#if UNITY_EDITOR
    public BombDevice DevBombPrefab => m_bombPrefab;
#endif

    public bool IsActive => m_bomb != null;

    public bool AnnounceOnBegin => false;

    public bool CanTrigger()
    {
        if (m_bombPrefab == null)
            return false;
        if (BombCrate.All.Count == 0)
            return false;
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_bombPrefab == null)
            return;

        if (!TryPickSpawn(out Vector3 position, out Quaternion rotation))
        {
            Debug.LogWarning("BombChaseEvent: 상자 주변에서 NavMesh를 찾지 못해 발동 취소", this);
            return;
        }

        m_bomb = Instantiate(m_bombPrefab, position, rotation);
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_bomb.GetComponent<NetworkObject>().Spawn();

        m_bomb.OnExploded += HandleExploded;
        m_resolved = false;

        m_bomb.ServerDeploy();
    }

    public void ServerTick()
    {
        if (m_bomb == null)
            return;

        if (m_resolved && Time.time >= m_despawnAt)
            Despawn();
    }

    public void ServerReset()
    {
        Despawn();
    }

    private void HandleExploded()
    {
        if (m_resolved)
            return;

        m_resolved = true;
        m_despawnAt = Time.time + m_resolvedLingerSeconds;
        Debug.Log("[돌발이벤트] 추격 폭탄 — 폭발");
    }

    private void Despawn()
    {
        if (m_bomb == null)
            return;

        m_bomb.OnExploded -= HandleExploded;
        SuddenEventUtil.DespawnOrDestroy(m_bomb.gameObject);
        m_bomb = null;
        m_resolved = false;
    }

    /// <summary>상자를 무작위 순서로 보며 NavMesh 위 스폰 지점을 찾는다. 모두 실패하면 false.</summary>
    private bool TryPickSpawn(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        int count = BombCrate.All.Count;
        if (count == 0)
            return false;

        int start = Random.Range(0, count);
        for (int i = 0; i < count; i++)
        {
            BombCrate crate = BombCrate.All[(start + i) % count];
            if (crate == null)
                continue;

            if (!NavMesh.SamplePosition(crate.SpawnPosition, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
                continue;

            position = hit.position;
            rotation = crate.SpawnRotation;
            return true;
        }

        return false;
    }
}
