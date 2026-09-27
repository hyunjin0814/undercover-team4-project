using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 추격 표적 선정 — 후보 탐색·자격 판정·재추격 쿨다운을 담당한다. 이동은 다루지 않는다.
/// </summary>
public class ChaseTargeting
{
    private readonly NpcChaseConfig m_config;
    private readonly NpcRepathScheduler m_repath;
    private readonly List<Transform> m_candidateBuffer = new List<Transform>();

    private readonly Dictionary<Transform, float> m_cooldowns = new Dictionary<Transform, float>();

    public ChaseTargeting(NpcChaseConfig config, NpcRepathScheduler repath)
    {
        m_config = config;
        m_repath = repath;
    }

    /// <summary>상태 진입 시 스캔 주기와 쿨다운 장부를 비운다.</summary>
    public void Reset()
    {
        m_cooldowns.Clear();

        m_repath.ForceDue(NpcRepathChannel.TargetScan);
    }

    /// <summary>이 플레이어를 당분간 노리지 않는다 — 격퇴, 그리고 도달 불가로 놓은 경우.</summary>
    public void PutOnCooldown(Transform target, float now)
    {
        if (target != null)
            m_cooldowns[target] = now + m_config.RetargetCooldown;
    }

    /// <summary>거리를 빼고 붙들 자격만 본다 — 존재·행동 가능·격퇴 쿨다운 아님.</summary>
    public bool IsHeld(Transform target, float now)
    {
        if (target == null)
            return false;
        if (IsOnCooldown(target, now))
            return false;

        PlayerHealth health = target.GetComponent<PlayerHealth>();
        return health != null && health.IsTargetable;
    }

    /// <summary>자격과 포기 거리를 함께 보고 계속 쫓아도 되는지 판정한다.</summary>
    public bool IsChaseable(Vector3 from, Transform target, float now) =>
        IsHeld(target, now)
        && ChaseMath.FlatDistance(from, target.position) <= m_config.ReleaseDistance;

    /// <summary>현재 표적보다 SwitchAdvantage만큼 더 가까운 후보를 찾는다. 없으면 null.</summary>
    public Transform FindCloser(Vector3 from, Transform current, float currentDistance, float now)
    {
        Transform nearest = PickNearest(from, now);
        if (nearest == null || nearest == current)
            return null;

        float distance = ChaseMath.FlatDistance(from, nearest.position);
        return currentDistance - distance >= m_config.SwitchAdvantage ? nearest : null;
    }

    /// <summary>범위 안 행동 가능한 플레이어 중 가장 가까운 사람을 고른다(쿨다운·스캔 주기 적용). 없으면 null.</summary>
    public Transform PickNearest(Vector3 from, float now)
    {
        if (!m_repath.Due(NpcRepathChannel.TargetScan))
            return null;

        SuddenEventUtil.CollectFieldPlayers(from, m_config.Range, m_candidateBuffer);
        for (int i = m_candidateBuffer.Count - 1; i >= 0; i--)
        {
            if (IsOnCooldown(m_candidateBuffer[i], now))
                m_candidateBuffer.RemoveAt(i);
        }

        Transform nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < m_candidateBuffer.Count; i++)
        {
            Transform candidate = m_candidateBuffer[i];
            float distance = ChaseMath.FlatDistance(from, candidate.position);
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = candidate;
        }

        return nearest;
    }

    private bool IsOnCooldown(Transform target, float now) =>
        m_cooldowns.TryGetValue(target, out float until) && now < until;
}
