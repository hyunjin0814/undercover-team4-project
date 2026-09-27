using UnityEngine;

/// <summary>
/// 침입(Intruding) 상태 — 목표 자물쇠까지 걸어가 해제 채널링을 채우면 도착을 통보한다(GDD 6-4).
/// 경로 실패는 도착이 아닌 실패로 통보한다.
/// </summary>
public class NpcIntrudeState : NpcStateBase
{
    private const float k_arriveDistance = 0.5f;

    private bool m_finished;
    private bool m_unlocking;
    private float m_unlockEndTime;

    public NpcIntrudeState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_finished = false;
        m_unlocking = false;
        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = 0f;

        if (m_owner.Intruder.IntrudeTarget == null)
        {
            Debug.LogWarning(
                $"NpcIntrudeState: 침입 목표가 없음 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
            return;
        }

        if (!m_owner.Agent.SetDestination(m_owner.Intruder.IntrudeTarget.position))
        {
            Debug.LogWarning(
                $"NpcIntrudeState: 침입 경로 실패 — 불발 처리: {m_owner.name}",
                m_owner
            );
            Finish(false);
        }
    }

    public override void Tick()
    {
        if (m_finished)
            return;

        if (m_unlocking)
        {
            if (Time.time >= m_unlockEndTime)
                Finish(true);
            return;
        }

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        BeginUnlock();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    private void BeginUnlock()
    {
        m_unlocking = true;
        m_unlockEndTime = Time.time + m_owner.Intruder.IntrudeUnlockSeconds;

        StopAgent();
        m_owner.Intruder.NotifyIntrudeUnlockStarted();
    }

    private void Finish(bool reached)
    {
        m_finished = true;

        StopAgent();
        m_owner.Intruder.NotifyIntrudeFinished(reached);
    }

    private void StopAgent()
    {
        m_owner.SetAgentStopped(true);
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
