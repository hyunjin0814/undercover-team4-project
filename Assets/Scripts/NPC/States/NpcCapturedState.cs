using UnityEngine;

/// <summary>
/// 체포(Captured) 상태 — 이동을 멈추고, 일정 시간 인계되지 않으면 일어나 도주한다(GDD 7-6).
/// 판정 완료·유치장 안 대상은 방치 타이머에서 제외한다.
/// </summary>
public class NpcCapturedState : NpcStateBase
{
    private float m_escapeTime;

    private readonly NpcCapturedConfig m_config;

    public NpcCapturedState(NpcController owner, NpcCapturedConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        if (m_owner.AgentReady)
        {
            m_owner.SetAgentStopped(true);
            m_owner.Agent.ResetPath();
        }

        m_escapeTime = Time.time + m_config.EscapeSeconds;
    }

    public override void Tick()
    {
        if (StaysPut)
        {
            return;
        }

        if (m_owner.StandUp.IsStandingUp)
            return;

        float remaining = m_escapeTime - Time.time;

        if (remaining <= 0f)
        {
            Escape();
            return;
        }
    }

    public override void Exit()
    {
        if (m_owner.AgentReady)
            m_owner.SetAgentStopped(false);
    }

    private bool StaysPut => NpcStateRules.StaysPutWhenFreed(m_owner);

    /// <summary>방치 타이머 만료 시 일어나기 모션 후 풀려나 도주한다.</summary>
    private void Escape()
    {
        m_owner.StandUp.ServerStandUpThen(Flee);
    }

    /// <summary>일어난 뒤 실제로 달아난다.</summary>
    private void Flee()
    {
        if (StaysPut)
            return;

        if (m_owner.Reaction.IsSprinter)
        {
            Debug.Log($"인계 방치 — 풀려나 질주 재개: {m_owner.name}");
            m_owner.Reaction.StartSprint();
            return;
        }

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position,
            m_owner.Reaction.ThreatSearchRadius
        );

        if (nearest != null)
        {
            if (m_owner.Reaction.IsRelentless)
            {
                Debug.Log($"인계 방치 — 풀려나 저항 재개: {m_owner.name}");
                m_owner.Reaction.StartResist(nearest.transform, relentless: true);
                return;
            }

            Debug.Log($"인계 방치 — 풀려나 도주: {m_owner.name}");
            m_owner.Reaction.StartFlee(nearest.transform);
            return;
        }

        Debug.Log($"인계 방치 — 풀려나 배회 복귀(주변에 플레이어 없음): {m_owner.name}");
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}
