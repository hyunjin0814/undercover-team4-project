using UnityEngine;

/// <summary>
/// 밀수 운반(Smuggling) 상태 — 화물을 지고 거래 지점까지 걸어가 도착을 통보한다(GDD 6-4).
/// 위협에 반응하지 않으며, 경로 실패는 실패로 통보한다.
/// </summary>
public class NpcSmuggleState : NpcStateBase
{
    private const float k_arriveDistance = 0.5f;

    private const float k_repathInterval = 0.5f;

    private SmugglerCargo m_cargo;
    private float m_baseSpeed;
    private float m_nextRepathTime;
    private bool m_finished;

    public NpcSmuggleState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_finished = false;
        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = 0f;

        m_cargo = m_owner.GetComponent<SmugglerCargo>();

        m_baseSpeed = m_owner.Agent.speed;
        ApplySpeed();

        if (m_cargo == null || m_cargo.Destination == null)
        {
            Debug.LogWarning(
                $"NpcSmuggleState: 맨홀 지점이 없음 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
            return;
        }

        if (!m_owner.Agent.SetDestination(m_cargo.Destination.position))
        {
            Debug.LogWarning(
                $"NpcSmuggleState: 운반 경로 실패 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
        }
    }

    public override void Tick()
    {
        if (m_finished || !m_owner.AgentReady)
            return;

        ApplySpeed();

        if (m_owner.Agent.pathPending)
            return;

        if (!m_owner.Agent.hasPath)
        {
            Repath();
            return;
        }

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Finish(true);
    }

    private void Repath()
    {
        if (m_cargo == null || m_cargo.Destination == null || Time.time < m_nextRepathTime)
            return;

        m_nextRepathTime = Time.time + k_repathInterval;
        m_owner.Agent.SetDestination(m_cargo.Destination.position);
    }

    private void ApplySpeed()
    {
        if (m_cargo != null)
            m_owner.Agent.speed = m_baseSpeed * m_cargo.SpeedMultiplier;
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

    private void Finish(bool reached)
    {
        m_finished = true;

        m_owner.SetAgentStopped(true);
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        if (m_cargo != null)
            m_cargo.NotifyFinished(m_owner, reached);
    }
}
