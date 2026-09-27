using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 원한 구역 수용(Detained) 상태 — 오검거당한 시민이 구역까지 걸어가 대기하다 임계 도달 시 추격으로 출동한다.
/// 이송 중에는 로컬 회피를 끈다.
/// </summary>
public class NpcDetainedState : NpcStateBase
{
    private const float k_arriveDistance = 0.9f;

    private const float k_slotSnapRadius = 0.8f;

    private bool m_arrived;

    private ObstacleAvoidanceType m_travelAvoidance;

    public NpcDetainedState(NpcController owner)
        : base(owner) { }

    public override void Enter()
    {
        m_arrived = false;
        m_travelAvoidance = m_owner.Agent.obstacleAvoidanceType;
        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = 0f;

        m_owner.Agent.obstacleAvoidanceType = ObstacleAvoidanceType.NoObstacleAvoidance;

        if (m_owner.Penalty.DetentionSpot == null)
        {
            Arrive();
            return;
        }

        if (!m_owner.Agent.SetDestination(SpotDestination()))
        {
            Debug.LogWarning(
                $"NpcDetainedState: 원한 구역 경로 실패 — 그 자리에서 수용 처리: {m_owner.name}",
                m_owner
            );
            Arrive();
        }
    }

    public override void Tick()
    {
        if (m_arrived)
            return;

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        Arrive();
    }

    public override void Exit()
    {
        m_owner.Agent.obstacleAvoidanceType = m_travelAvoidance;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    private void Arrive()
    {
        m_arrived = true;

        m_owner.SetAgentStopped(true);
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }

    private Vector3 SpotDestination() =>
        GatherSlot.Resolve(
            m_owner.Penalty.DetentionSpot.position,
            m_owner.Penalty.DetentionSlotOffset,
            NavMesh.AllAreas,
            k_slotSnapRadius
        );
}
