using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 반경 안에 동료가 없는 상태가 일정 시간 이어진 현장 플레이어를 추적하는 직렬화 값 묶음.
/// 쓰는 쪽이 Update에서 Tick을 굴리고, 납치 이벤트가 표적 후보로 읽는다.
/// </summary>
[System.Serializable]
public class LonePlayerWatch
{
    [Tooltip("이 반경(m) 안에 다른 현장 플레이어가 없으면 혼자로 본다")]
    [Min(1f)]
    [SerializeField] private float m_loneRadius = 15f;

    [Tooltip("혼자인 상태가 이 시간(초) 이상 이어져야 표적이 된다 — 잠깐 갈라진 순간에 걸리지 않게")]
    [Min(0f)]
    [SerializeField] private float m_loneSeconds = 20f;

    [Tooltip("본부 구역 (비우면 씬에서 자동 탐색) — 본부 안의 플레이어는 표적에서 제외한다")]
    [SerializeField] private HqOccupancyZone m_hqZone;

    [Tooltip("혼자 판정 갱신 주기(초) — 매 프레임 돌 필요가 없다")]
    [SerializeField] private float m_scanInterval = 0.25f;

    private readonly Dictionary<PlayerHealth, float> m_aloneSince = new Dictionary<PlayerHealth, float>();

    private readonly List<PlayerHealth> m_staleBuffer = new List<PlayerHealth>();

    private float m_scanCooldown;

    /// <summary>비어 있는 씬 참조를 채운다 — 쓰는 쪽 Awake에서 한 번 부른다.</summary>
    public void ResolveSceneRefs()
    {
        if (m_hqZone == null)
            m_hqZone = UnityEngine.Object.FindFirstObjectByType<HqOccupancyZone>();
    }

    /// <summary>타이머를 굴린다 — 쓰는 쪽 Update에서 매 프레임 부른다(갱신 주기는 안에서 지킨다).</summary>
    public void Tick(float deltaTime)
    {
        m_scanCooldown -= deltaTime;
        if (m_scanCooldown > 0f)
            return;
        m_scanCooldown = m_scanInterval;

        TickLoneTimers();
    }

    /// <summary>지속 조건까지 채운 후보 중 가장 오래 혼자인 플레이어 — 없으면 null.</summary>
    public Transform FindTarget()
    {
        Transform best = null;
        float oldest = float.MaxValue;

        foreach (KeyValuePair<PlayerHealth, float> entry in m_aloneSince)
        {
            if (entry.Key == null)
                continue;
            if (Time.time - entry.Value < m_loneSeconds)
                continue;

            if (entry.Value < oldest)
            {
                oldest = entry.Value;
                best = entry.Key.transform;
            }
        }

        return best;
    }

    /// <summary>지속 조건 없이 표적을 하나 고른다(강제 발동 전용). 혼자인 현장 플레이어를 우선한다.</summary>
    public Transform FindForcedTarget()
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        for (int i = 0; i < players.Count; i++)
            if (IsLoneCandidate(players[i], players))
                return players[i].transform;

        for (int i = 0; i < players.Count; i++)
            if (players[i] != null && players[i].IsTargetable)
                return players[i].transform;

        return null;
    }

    /// <summary>쌓인 타이머를 모두 버린다 — 표적을 소비했거나 강제 정리할 때. 다음 Tick부터 처음부터 센다.</summary>
    public void Reset()
    {
        m_aloneSince.Clear();
        m_scanCooldown = 0f;
    }

    private void TickLoneTimers()
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];

            if (IsLoneCandidate(player, players))
            {
                if (!m_aloneSince.ContainsKey(player))
                    m_aloneSince[player] = Time.time;
            }
            else
            {
                m_aloneSince.Remove(player);
            }
        }

        m_staleBuffer.Clear();
        foreach (PlayerHealth tracked in m_aloneSince.Keys)
            if (tracked == null)
                m_staleBuffer.Add(tracked);

        for (int i = 0; i < m_staleBuffer.Count; i++)
            m_aloneSince.Remove(m_staleBuffer[i]);
    }

    private bool IsLoneCandidate(PlayerHealth player, IReadOnlyList<PlayerHealth> scanned)
    {
        if (player == null || !player.IsTargetable)
            return false;

        if (m_hqZone != null && m_hqZone.Contains(player))
            return false;

        Vector3 origin = player.transform.position;
        float radiusSqr = m_loneRadius * m_loneRadius;

        for (int i = 0; i < scanned.Count; i++)
        {
            PlayerHealth other = scanned[i];
            if (other == null || other == player || !other.IsTargetable)
                continue;

            if ((other.transform.position - origin).sqrMagnitude <= radiusSqr)
                return false;
        }

        return true;
    }
}
