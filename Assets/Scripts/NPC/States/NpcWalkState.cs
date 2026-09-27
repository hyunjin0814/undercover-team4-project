using UnityEngine;

/// <summary>
/// 배회(Walk) 상태 — 주변의 도달 가능한 지점으로 걸어가고, 도착하거나 막히면 대기·재추첨한다.
/// </summary>
public class NpcWalkState : NpcStateBase
{
    private const int k_maxSampleAttempts = 10;
    private const float k_arriveThreshold = 0.5f;

    private const float k_stuckMinProgress = 0.3f;

    private const int k_stuckStrikesToRepick = 4;

    private const int k_maxReachabilityProbes = 4;

    private const float k_navSampleMaxDistance = 2f;

    private Vector3 m_lastProgressPosition;
    private int m_stuckStrikes;

    private readonly NpcWalkConfig m_config;

    public NpcWalkState(NpcController owner, NpcWalkConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.Repath.MarkDone(NpcRepathChannel.StuckCheck);
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;

        SetNextWanderPoint();
    }

    public override void Tick()
    {
        if (!m_owner.Agent.pathPending &&
            m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        {
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        TickStuckWatch();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    /// <summary>실제 이동 거리로 막힘을 감시해 제자리면 목적지를 재추첨한다.</summary>
    private void TickStuckWatch()
    {
        if (!m_owner.Repath.Due(NpcRepathChannel.StuckCheck))
            return;

        Vector3 position = m_owner.transform.position;
        float progress = Vector3.Distance(position, m_lastProgressPosition);
        m_lastProgressPosition = position;

        if (progress >= k_stuckMinProgress)
        {
            m_stuckStrikes = 0;
            return;
        }

        if (++m_stuckStrikes < k_stuckStrikesToRepick)
            return;
        m_stuckStrikes = 0;

        Debug.Log(
            $"배회 막힘 — 위치 {position:F1} / 목적지 {m_owner.Agent.destination:F1} / "
                + $"경로 {m_owner.Agent.pathStatus}, 지점 재추첨: {m_owner.name}",
            m_owner
        );

        SetNextWanderPoint();
    }

    private void SetNextWanderPoint()
    {
        if (!NpcMovePoint.TryPick(
                m_owner.Agent,
                m_config.MinWanderDistance,
                m_config.WanderRadius,
                k_navSampleMaxDistance,
                k_maxSampleAttempts,
                k_maxReachabilityProbes,
                out Vector3 point,
                out bool reachable
            ))
            return;

        if (!reachable)
        {
            Debug.Log(
                $"배회 지점 전부 도달 불가 — 위치 {m_owner.transform.position:F1}: {m_owner.name}",
                m_owner
            );
        }

        m_owner.Agent.SetDestination(point);
    }
}
