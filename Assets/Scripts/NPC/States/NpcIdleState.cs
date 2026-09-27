using UnityEngine;

/// <summary>
/// 대기(Idle) 상태 — 정해진 시간 동안 멈춰 있다가 배회(Walk)로 전환한다.
/// </summary>
public class NpcIdleState : NpcStateBase
{
    private float m_waitTimer;

    private readonly NpcIdleConfig m_config;

    public NpcIdleState(NpcController owner, NpcIdleConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.SetAgentStopped(true);

        bool isLongIdle = Random.value < m_config.LongIdleChance;
        m_waitTimer = isLongIdle
            ? Random.Range(m_config.LongIdleTimeMin, m_config.LongIdleTimeMax)
            : Random.Range(m_config.IdleTimeMin, m_config.IdleTimeMax);
    }

    public override void Tick()
    {
        m_waitTimer -= Time.deltaTime;
        if (m_waitTimer <= 0f)
            m_owner.StateMachine.ChangeState(NpcState.Walk);
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
            m_owner.SetAgentStopped(false);
    }
}
