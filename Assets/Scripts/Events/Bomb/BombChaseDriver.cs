using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 추격 폭탄의 NavMesh 이동 — 가장 가까운 현장 플레이어를 쫓거나 배회한다.
/// 언제 쫓을지는 BombDevice가 정하며, 동기화 상태는 없다.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class BombChaseDriver : MonoBehaviour
{
    [Tooltip("추격 속도(m/s) — 플레이어 걷기(5)보다 빠르고 달리기(8)보다 확실히 느리게 둘 것")]
    [SerializeField]
    private float m_chaseSpeed = 5.5f;

    [Tooltip("이 반경(m) 안에 현장 인원이 들어오면 잠에서 깨어 추격과 카운트다운을 함께 시작한다")]
    [SerializeField]
    private float m_wakeRadius = 14f;

    [Header("배회 (#993)")]
    [Tooltip("표적을 찾기 전 도시를 도는 속도(m/s) — 추격 속도보다 확실히 느려야 한다. " +
             "같으면 '발견하고 달려든다'는 전환이 안 읽혀서 배회가 그냥 느린 추격이 된다")]
    [SerializeField]
    private float m_roamSpeed = 4.2f;

    [Tooltip("배회 목적지를 뽑는 반경(m) — 현재 위치 기준. 크게 잡을수록 한 번에 멀리 간다")]
    [SerializeField]
    private float m_roamPointRadius = 35f;

    [Tooltip("배회 목적지에 이만큼(m) 다가오면 다음 지점을 뽑는다")]
    [SerializeField]
    private float m_roamArriveDistance = 1.5f;

    [Tooltip("배회 지점 추첨 시도 횟수 — 다 실패하면 다음 틱에 다시 시도한다")]
    [SerializeField]
    private int m_roamSampleAttempts = 8;

    [Tooltip("표적을 다시 고르고 목적지를 갱신하는 주기(초)")]
    [SerializeField]
    private float m_retargetInterval = 0.5f;

    [Tooltip("현재 표적보다 이만큼(m) 더 가까워야 표적을 바꾼다 — 두 사람 사이에서 갈팡질팡하지 않게")]
    [SerializeField]
    private float m_retargetHysteresis = 1.5f;

    [Tooltip("이 반경(m) 안의 현장 플레이어만 표적이 된다 — 맵 전역을 덮을 만큼 크게 둘 것")]
    [SerializeField]
    private float m_targetSearchRadius = 300f;

    private NavMeshAgent m_agent;

    private PlayerHealth m_target;
    private float m_nextRetargetTime;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_agent.speed = m_chaseSpeed;
    }

    /// <summary>원격 피어의 NavMeshAgent를 끈다.</summary>
    public void DisableAgent()
    {
        m_agent.enabled = false;
    }

    /// <summary>깨우기 반경 안에 현장 인원이 있는지 재타겟 주기마다 검사한다.</summary>
    public bool PollWakeTrigger()
    {
        if (Time.time < m_nextRetargetTime)
            return false;

        m_nextRetargetTime = Time.time + m_retargetInterval;
        return SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_wakeRadius) != null;
    }

    /// <summary>무장 시점에 시계를 비운다 — 다음 <see cref="Tick"/>에서 곧바로 표적을 고른다.</summary>
    public void ResetRetargetClock()
    {
        m_nextRetargetTime = 0f;

        if (m_agent != null)
            m_agent.speed = m_chaseSpeed;
    }

    /// <summary>대기 중 현재 위치 기준으로 배회 지점을 뽑아 돌아다닌다. 도착하면 다시 뽑는다.</summary>
    public void TickRoam()
    {
        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.speed = m_roamSpeed;
        m_agent.isStopped = false;

        if (m_agent.pathPending)
            return;
        if (m_agent.hasPath && m_agent.remainingDistance > m_roamArriveDistance)
            return;

        PickRoamPoint();
    }

    private void PickRoamPoint()
    {
        for (int i = 0; i < m_roamSampleAttempts; i++)
        {
            Vector2 offset = Random.insideUnitCircle * m_roamPointRadius;
            Vector3 candidate = transform.position + new Vector3(offset.x, 0f, offset.y);

            NavMeshHit hit;
            if (!NavMesh.SamplePosition(candidate, out hit, m_roamPointRadius, m_agent.areaMask))
                continue;

            if ((hit.position - transform.position).sqrMagnitude < m_roamArriveDistance * m_roamArriveDistance)
                continue;

            m_agent.SetDestination(hit.position);
            return;
        }
    }

    /// <summary>표적 재선정 + 목적지 갱신. 표적도 움직이므로 같은 주기로 목적지를 다시 찍는다.</summary>
    public void Tick()
    {
        if (Time.time < m_nextRetargetTime)
            return;

        m_nextRetargetTime = Time.time + m_retargetInterval;

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_targetSearchRadius);
        if (nearest == null)
        {
            m_target = null;
            Stop();
            return;
        }

        if (m_target == null || !m_target.IsTargetable)
        {
            m_target = nearest;
        }
        else if (nearest != m_target)
        {
            float current = Vector3.Distance(transform.position, m_target.transform.position);
            float candidate = Vector3.Distance(transform.position, nearest.transform.position);
            if (candidate + m_retargetHysteresis < current)
                m_target = nearest;
        }

        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.isStopped = false;
        m_agent.SetDestination(m_target.transform.position);
    }

    /// <summary>그 자리에 멈춘다 — 쫓을 사람이 없을 때와 폭발 시점에 불린다.</summary>
    public void Stop()
    {
        if (!m_agent.enabled || !m_agent.isOnNavMesh)
            return;

        m_agent.ResetPath();
        m_agent.isStopped = true;
    }
}
