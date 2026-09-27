using UnityEngine;

/// <summary>
/// 넉백 착지 KO용 기절(Stunned) 상태 — 지속 시간 동안 멈췄다가 일어나 도주한다(GDD 7-4).
/// 테이저·체력 0 기절은 NpcStun 오버레이를 쓴다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    private bool m_standingUp;

    private readonly NpcStunConfig m_config;

    public NpcStunnedState(NpcController owner, NpcStunConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_timer = 0f;
        m_standingUp = false;
        m_owner.Stun.SetRising(false);
        SetAgentStopped(true);
    }

    public override void Tick()
    {
        m_timer += Time.deltaTime;

        if (!m_standingUp && m_timer >= m_config.StunSeconds)
        {
            m_standingUp = true;
            m_owner.Stun.SetRising(true);
            m_owner.RaiseStandUp();
        }

        if (m_timer >= m_config.StunSeconds + m_config.StandUpSeconds)
        {
            m_owner.Health.ServerRestoreToOne();
            m_owner.Reaction.ResumeReaction(m_owner.Reaction.ThreatTarget);
        }
    }

    public override void Exit()
    {
        SetAgentStopped(false);
        m_owner.Stun.SetRising(false);
    }

    /// <summary>에이전트가 준비됐을 때만 isStopped를 설정한다.</summary>
    private void SetAgentStopped(bool stopped)
    {
        if (!m_owner.AgentReady)
            return;

        UnityEngine.AI.NavMeshAgent agent = m_owner.Agent;
        agent.isStopped = stopped;
        if (stopped)
            agent.ResetPath();
    }
}
