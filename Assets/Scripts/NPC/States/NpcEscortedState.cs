using UnityEngine;

/// <summary>
/// 연행(Escorted) 상태 — 끄는 플레이어를 따라 이동하고, 너무 멀어지면 Captured로 돌아간다.
/// </summary>
public class NpcEscortedState : NpcStateBase
{
    private const float k_repathMoveThreshold = 0.5f;

    private const float k_resumeDistanceOffset = 0.75f;

    private Vector3 m_lastTargetPos;
    private float m_baseSpeed;
    private bool m_isHolding;

    private readonly NpcEscortConfig m_config;

    public NpcEscortedState(NpcController owner, NpcEscortConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_isHolding = false;

        if (m_owner.Rope.IsRoped || !m_owner.AgentReady)
            return;

        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = m_config.FollowDistance;

        if (m_owner.Custody.EscortTarget != null)
        {
            m_lastTargetPos = m_owner.Custody.EscortTarget.position;
            m_owner.Agent.SetDestination(m_owner.Custody.EscortTarget.position);
            m_owner.Repath.MarkDone(NpcRepathChannel.Repath);
        }
    }

    public override void Tick()
    {
        if (m_owner.Rope.IsRoped)
            return;

        Transform target = m_owner.Custody.EscortTarget;
        if (target == null)
        {
            m_owner.Custody.StopEscort();
            return;
        }

        float distance = Vector3.Distance(m_owner.transform.position, target.position);

        if (distance > m_config.BreakDistance)
        {
            Debug.Log($"연행 해제 — 거리 이탈 ({distance:F1}m): {m_owner.name}");
            m_owner.Custody.StopEscort();
            return;
        }

        if (m_isHolding)
        {
            if (distance > m_config.FollowDistance + k_resumeDistanceOffset)
            {
                m_isHolding = false;
                m_owner.SetAgentStopped(false);
                m_lastTargetPos = target.position;
                m_owner.Agent.SetDestination(target.position);
                m_owner.Repath.MarkDone(NpcRepathChannel.Repath);
            }
            return;
        }

        if (distance <= m_config.FollowDistance)
        {
            m_isHolding = true;
            m_owner.SetAgentStopped(true);
            m_owner.Agent.velocity = Vector3.zero;
            if (m_owner.Agent.isOnNavMesh)
                m_owner.Agent.ResetPath();
            return;
        }

        m_owner.Agent.speed =
            distance > m_config.BoostDistance
                ? m_baseSpeed * m_config.BoostMultiplier
                : m_baseSpeed;

        bool moved =
            (target.position - m_lastTargetPos).sqrMagnitude
            >= k_repathMoveThreshold * k_repathMoveThreshold;
        if (moved && m_owner.Repath.Due(NpcRepathChannel.Repath))
        {
            m_lastTargetPos = target.position;
            m_owner.Agent.SetDestination(target.position);
        }
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }
}
