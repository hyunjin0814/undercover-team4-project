using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 오검거 호송(PenaltyEscorting) 상태 — 선두는 목적지로 걷고 나머지는 선두 기준 대형 오프셋을 따라간다.
/// 납치 임무일 때만 오프셋 목적지를 NavMesh에 스냅한다.
/// </summary>
public class NpcPenaltyEscortState : NpcStateBase
{
    private const float k_leaderStopDistance = 1.2f;

    private const float k_offsetSnapRadius = 2f;

    private static NavMeshPath s_reachabilityProbe;

    private float m_baseSpeed;

    private readonly NpcEscortConfig m_config;

    public NpcPenaltyEscortState(NpcController owner, NpcEscortConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Repath.ForceDue(NpcRepathChannel.Repath);

        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance =
            m_owner.Penalty.PenaltyEscortLeader == null ? k_leaderStopDistance : 0.1f;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        if (!m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

        NpcController leader = m_owner.Penalty.PenaltyEscortLeader;

        if (leader == null || leader.Penalty.PenaltyEscortGoal == null)
        {
            if (m_owner.Penalty.PenaltyEscortGoal != null)
                m_owner.Agent.SetDestination(m_owner.Penalty.PenaltyEscortGoal.position);
            return;
        }

        Vector3 spot = ResolveFollowSpot(leader, out bool reachable);
        m_owner.Agent.SetDestination(spot);

        float lag = Vector3.Distance(m_owner.transform.position, spot);
        m_owner.Agent.speed =
            reachable && lag > m_config.BoostDistance ? m_baseSpeed * m_config.BoostMultiplier : m_baseSpeed;
    }

    private Vector3 ResolveFollowSpot(NpcController leader, out bool reachable)
    {
        reachable = true;
        Vector3 offset = m_owner.Penalty.PenaltyEscortOffset;
        Vector3 spot = leader.transform.TransformPoint(offset);

        if (!m_owner.Penalty.IsAbductionDuty || offset == Vector3.zero)
            return spot;

        if (NavMesh.SamplePosition(spot, out NavMeshHit hit, k_offsetSnapRadius, m_owner.Agent.areaMask)
            && HasCompletePath(m_owner.Agent, hit.position))
        {
            return hit.position;
        }

        reachable = false;
        return leader.transform.position;
    }

    private static bool HasCompletePath(NavMeshAgent agent, Vector3 destination)
    {
        s_reachabilityProbe ??= new NavMeshPath();
        return NavMesh.CalculatePath(agent.transform.position, destination, agent.areaMask, s_reachabilityProbe)
            && s_reachabilityProbe.status == NavMeshPathStatus.PathComplete;
    }
}
