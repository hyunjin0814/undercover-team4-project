using UnityEngine;

/// <summary>
/// 수감(Jailed) 상태 — 배치 지점으로 옮겨진 뒤 감옥 방 안을 배회한다(GDD 7-2).
/// 탈옥 방출이나 반출로만 빠져나간다.
/// </summary>
public class NpcJailedState : NpcStateBase
{
    private const float k_fallbackRadius = 1.6f;

    private const float k_arriveSlack = 0.15f;

    private const float k_bodySnapRadius = 3f;

    private const float k_roomCheckInterval = 0.5f;

    private const float k_pauseSecondsMin = 1.5f;
    private const float k_pauseSecondsMax = 5f;

    private float m_nextMoveTime;

    private float m_nextRoomCheckTime;

    private bool m_walking;

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        BeginPause();

        if (m_owner.Custody.JailSpot == null)
            return;

        if (JailRoom.Contains(m_owner.transform.position))
            return;

        if (m_owner.Ragdoll != null && m_owner.Ragdoll.ServerPlaceRagdollBody(RagdollPlacement()))
            return;

        if (m_owner.Agent.Warp(m_owner.Custody.JailSpot.position))
        {
            m_owner.transform.rotation = SpotRotation();
            return;
        }

        if (!m_owner.Agent.enabled)
            return;

        Debug.LogWarning(
            $"NpcJailedState: 배치 지점으로 워프 실패 — 감옥 밖에 남는다. "
                + $"지점이 감옥 NavMesh 위에 있는지 확인할 것: {m_owner.Custody.JailSpot.name}",
            m_owner
        );
    }

    /// <summary>감옥 방 전체에서 목적지를 골라 배회한다. 서버(또는 오프라인)에서만 움직인다.</summary>
    public override void Tick()
    {
        if (m_owner.Custody.JailSpot == null || !m_owner.Agent.isOnNavMesh)
            return;

        if (m_owner.StandUp.IsStandingUp)
            return;

        if (TryReturnToRoom())
            return;

        if (m_walking)
        {
            if (m_owner.Agent.pathPending)
                return;

            if (m_owner.Agent.remainingDistance > m_owner.Agent.stoppingDistance + k_arriveSlack)
                return;

            BeginPause();
            return;
        }

        if (Time.time < m_nextMoveTime)
            return;

        BeginWander();
    }

    public override void Exit()
    {
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    private void BeginWander()
    {
        if (!JailRoom.TryRandomPoint(m_owner.Agent.areaMask, out Vector3 target)
            && !TrySpotNeighbourhood(out target))
        {
            BeginPause();
            return;
        }

        m_owner.SetAgentStopped(false);
        if (!m_owner.Agent.SetDestination(target))
        {
            BeginPause();
            return;
        }

        m_walking = true;
    }

    private Vector3 RagdollPlacement()
    {
        Vector3 spot = m_owner.Custody.JailSpot.position;

        return UnityEngine.AI.NavMesh.SamplePosition(
            spot, out UnityEngine.AI.NavMeshHit hit, k_bodySnapRadius, UnityEngine.AI.NavMesh.AllAreas)
            ? hit.position
            : spot;
    }

    private bool TryReturnToRoom()
    {
        if (!JailRoom.HasRoom || Time.time < m_nextRoomCheckTime)
            return false;

        m_nextRoomCheckTime = Time.time + k_roomCheckInterval;

        if (JailRoom.Contains(m_owner.transform.position))
            return false;

        if (!m_owner.Agent.Warp(m_owner.Custody.JailSpot.position))
        {
            Debug.LogWarning(
                $"NpcJailedState: 방 밖으로 나간 수감자를 되돌리지 못했다 — {m_owner.name}", m_owner);
            return true;
        }

        m_owner.transform.rotation = SpotRotation();
        BeginPause();
        Debug.Log($"[감옥] 방 밖으로 나간 수감자를 배치 지점으로 되돌렸다: {m_owner.name}");
        return true;
    }

    private bool TrySpotNeighbourhood(out Vector3 target)
    {
        Vector2 offset = Random.insideUnitCircle * k_fallbackRadius;
        Vector3 candidate = m_owner.Custody.JailSpot.position + new Vector3(offset.x, 0f, offset.y);

        if (UnityEngine.AI.NavMesh.SamplePosition(
                candidate, out UnityEngine.AI.NavMeshHit hit, k_fallbackRadius, m_owner.Agent.areaMask))
        {
            target = hit.position;
            return true;
        }

        target = Vector3.zero;
        return false;
    }

    private void BeginPause()
    {
        StopMoving();
        m_nextMoveTime = Time.time + Random.Range(k_pauseSecondsMin, k_pauseSecondsMax);
    }

    private void StopMoving()
    {
        m_walking = false;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(true);
            m_owner.Agent.ResetPath();
        }
    }

    private Quaternion SpotRotation()
    {
        Vector3 forward = m_owner.Custody.JailSpot.forward;
        forward.y = 0f;
        return forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward)
            : m_owner.transform.rotation;
    }
}
