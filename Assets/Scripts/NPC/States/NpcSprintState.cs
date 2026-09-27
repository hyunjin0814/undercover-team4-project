using UnityEngine;

/// <summary>
/// 질주(Sprinting) 상태 — 도착할 때마다 새 목적지를 뽑아 멈추지 않고 뛰어다닌다(공연음란범용).
/// 속도·거리는 NpcFleeConfig 값을 빌려 쓴다.
/// </summary>
public class NpcSprintState : NpcStateBase
{
    private const float k_arriveThreshold = 0.5f;

    private const int k_maxSampleAttempts = 10;

    private const int k_maxReachabilityProbes = 4;

    private const float k_navSampleMaxDistance = 4f;

    private const float k_stuckMinProgress = 0.5f;

    private const int k_stuckStrikesToRepick = 2;

    private const float k_repickInterval = 0.5f;

    private readonly NpcFleeConfig m_config;

    private float m_baseSpeed;
    private float m_nextPickTime;
    private Vector3 m_lastProgressPosition;
    private int m_stuckStrikes;

    public NpcSprintState(NpcController owner, NpcFleeConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.SetAgentStopped(false);
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_config.SpeedMultiplier;

        m_owner.Repath.MarkDone(NpcRepathChannel.StuckCheck);
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;

        SetNextPoint();
    }

    public override void Tick()
    {
        if (
            !m_owner.Agent.pathPending
            && (!m_owner.Agent.hasPath
                || m_owner.Agent.remainingDistance <= m_owner.Agent.stoppingDistance + k_arriveThreshold)
        )
        {
            if (Time.time < m_nextPickTime)
                return;

            m_nextPickTime = Time.time + k_repickInterval;
            SetNextPoint();
            return;
        }

        TickStuckWatch();
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

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
        SetNextPoint();
    }

    private void SetNextPoint()
    {
        if (NpcMovePoint.TryPick(
                m_owner.Agent,
                m_config.StepDistance,
                m_config.FarPointDistance,
                k_navSampleMaxDistance,
                k_maxSampleAttempts,
                k_maxReachabilityProbes,
                out Vector3 point,
                out _
            ))
            m_owner.Agent.SetDestination(point);
    }
}
