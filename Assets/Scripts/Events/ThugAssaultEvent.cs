using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 괴한 습격 (돌발 이벤트 · 현장) — 현장 근처에 괴한이 나타나 플레이어를 습격한다. (GDD 6-4/7-4, #106)
/// 제압 대상이 아닌 순수 위협 이벤트다: 스폰된 <see cref="ThugAttacker"/>가 지속 시간 동안 플레이어를 추격·타격하고,
/// 시간이 지나면 스스로 물러난다(디스폰). 플레이어는 회피하거나, HP가 소진돼 다운되면 동료 구조(#105)에 의존한다.
///
/// 서버 권위 — 스폰·지속 관리는 서버(또는 오프라인)에서만. 스폰된 괴한은 NetworkObject라 전 클라에 복제되고,
/// HP 데미지는 PlayerData의 동기화 HP를 통해 반영된다. (#56)
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class ThugAssaultEvent : MonoBehaviour, ISuddenEvent
{
    [Header("괴한 프리팹")]
    [SerializeField] private ThugAttacker m_thugPrefab;

    [Header("지속 시간(초)")]
    [Tooltip("괴한이 습격을 지속하는 시간 — 지나면 물러난다(디스폰)")]
    [SerializeField] private float m_durationSeconds = 20f;

    [Header("스폰 위치 — 현장 플레이어 기준 거리(m)")]
    [SerializeField] private float m_spawnDistanceMin = 8f;
    [SerializeField] private float m_spawnDistanceMax = 15f;
    [Tooltip("스폰 후보 지점에서 이 거리(m) 안에 NavMesh가 없으면 그 지점은 버린다")]
    [SerializeField] private float m_navSampleMaxDistance = 4f;
    [Tooltip("유효한 스폰 지점을 찾는 최대 시도 횟수")]
    [SerializeField] private int m_maxSpawnAttempts = 8;

    private ThugAttacker m_thug;
    private float m_endTime;

    public string DisplayName => "괴한 습격";

    public bool IsActive => m_thug != null;

    public bool CanTrigger()
    {
        // 습격할 현장 플레이어가 있어야 성립한다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_thugPrefab == null)
        {
            Debug.LogWarning("ThugAssaultEvent: 괴한 프리팹이 지정되지 않음", this);
            return;
        }

        Transform player = SuddenEventUtil.FindRandomFieldPlayer();
        if (player == null)
            return;

        if (!SuddenEventUtil.TryFindSpawnPositionNear(
                player.position, m_spawnDistanceMin, m_spawnDistanceMax, m_navSampleMaxDistance, m_maxSpawnAttempts,
                out Vector3 spawnPosition))
        {
            Debug.LogWarning("ThugAssaultEvent: NavMesh 위 스폰 지점을 찾지 못해 괴한 습격 발생 취소", this);
            return;
        }

        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        m_thug = Instantiate(m_thugPrefab, spawnPosition, rotation);

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_thug.GetComponent<NetworkObject>().Spawn();

        m_endTime = Time.time + m_durationSeconds;
    }

    public void ServerTick()
    {
        if (m_thug == null)
            return;

        // 지속 시간이 끝나면 괴한이 물러난다
        if (Time.time >= m_endTime)
        {
            Debug.Log("[돌발이벤트] 괴한 습격 — 지속 시간 종료, 괴한 이탈");
            Despawn();
        }
    }

    public void ServerReset()
    {
        Despawn();
    }

    private void Despawn()
    {
        if (m_thug == null)
            return;

        SuddenEventUtil.DespawnOrDestroy(m_thug.gameObject);
        m_thug = null;
    }
}
