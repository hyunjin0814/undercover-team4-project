using UnityEngine;

/// <summary>
/// 연행(Escorted) 상태 — 체포 성공 직후 진입해 체포한 플레이어를 따라 이동한다. (이슈 #59)
/// 플레이어와 너무 멀어지면 그 자리에서 Captured로 돌아가 멈춘다.
/// 이번 빌드는 순응형(즉시 연행) 기준 — 도주형/저항형 반응은 후속 빌드(GDD 6-1).
/// </summary>
public class NpcEscortedState : NpcStateBase
{
    private const float k_repathInterval = 0.2f; // 경로 재계산 최소 간격(초) — 매 프레임 재계산 방지
    private const float k_repathMoveThreshold = 0.5f; // 목표가 이만큼(m) 움직였을 때만 재계산

    // 근접 정지 히스테리시스(m) — 멈춘 뒤 추종 거리보다 이만큼 더 멀어져야 재추종한다.
    // 정지/추종 경계가 하나면 그 근처에서 매 프레임 상태가 뒤집혀 떨림(jitter)이 생긴다 (#97)
    private const float k_resumeDistanceOffset = 0.75f;

    private float m_repathTimer;
    private Vector3 m_lastTargetPos;
    private float m_baseSpeed;
    private bool m_isHolding; // 플레이어 근접으로 정지 중인지 (#97)

    private readonly NpcEscortConfig m_config;

    public NpcEscortedState(NpcController owner, NpcEscortConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        // 원복용 기준 속도는 밧줄 분기보다 먼저 잡는다 — 안 그러면 Exit이 0으로 되돌려 놓는다
        m_baseSpeed = m_owner.Agent.speed;
        m_repathTimer = 0f;
        m_isHolding = false;

        // 밧줄로 끌려오는 중이면 에이전트가 꺼져 있다 — 추종 로직을 아예 돌리지 않는다.
        // 위치는 밧줄 장력(NpcController.TickRopeDrag)이 직접 제어한다. (#369, #390에서 NPC로 이관)
        if (m_owner.IsRoped)
            return;

        m_owner.Agent.isStopped = false;
        // 플레이어 등에 딱 붙지 않도록 추종 거리만큼 앞에서 멈춘다
        m_owner.Agent.stoppingDistance = m_config.FollowDistance;

        if (m_owner.EscortTarget != null)
        {
            m_lastTargetPos = m_owner.EscortTarget.position;
            m_owner.Agent.SetDestination(m_owner.EscortTarget.position);
        }
    }

    public override void Tick()
    {
        // 밧줄 끌기 중에는 추종·거리 이탈 판정을 돌리지 않는다 — 에이전트가 꺼져 있어 SetDestination이
        // 조용히 실패하고, 밧줄은 길이로 거리를 스스로 유지하므로 이탈 개념 자체가 없다.
        // 끌기 해제는 PlayerEscorter.TickTetherCleanup이 상태를 보고 판단한다. (#369)
        if (m_owner.IsRoped)
            return;

        Transform target = m_owner.EscortTarget;
        if (target == null)
        {
            // 대상 소실(플레이어 파괴 등) — 그 자리에서 체포 상태로 멈춘다
            m_owner.StopEscort();
            return;
        }

        float distance = Vector3.Distance(m_owner.transform.position, target.position);

        // 너무 멀어지면 연행이 풀리고 그 자리에서 체포된 채 멈춘다
        if (distance > m_config.BreakDistance)
        {
            Debug.Log($"연행 해제 — 거리 이탈 ({distance:F1}m): {m_owner.name}");
            m_owner.StopEscort();
            return;
        }

        // ---- 근접 정지 (#97) ----
        // stoppingDistance만으로는 목적지가 계속 갱신될 때 감속·재출발이 반복돼
        // 플레이어가 멈추거나 돌아설 때 미끄러지듯 파고드는 문제가 있다.
        // 추종 거리 안으로 들어오면 경로를 버리고 확실히 정지시킨다.
        if (m_isHolding)
        {
            // 히스테리시스 밖으로 벗어나야 재추종 — 경계에서 정지/추종이 떨리는 것 방지
            if (distance > m_config.FollowDistance + k_resumeDistanceOffset)
            {
                m_isHolding = false;
                m_owner.Agent.isStopped = false;
                m_repathTimer = 0f;
                m_lastTargetPos = target.position;
                m_owner.Agent.SetDestination(target.position);
            }
            return; // 정지 유지 — 아래 추종 로직은 건너뛴다
        }

        if (distance <= m_config.FollowDistance)
        {
            m_isHolding = true;
            m_owner.Agent.isStopped = true;
            m_owner.Agent.velocity = Vector3.zero; // 감속 관성까지 끊어 밀림 없이 그 자리에 선다
            if (m_owner.Agent.isOnNavMesh)
                m_owner.Agent.ResetPath();
            return;
        }

        // 뒤처지면 속도를 올려 따라잡는다
        m_owner.Agent.speed =
            distance > m_config.BoostDistance
                ? m_baseSpeed * m_config.BoostMultiplier
                : m_baseSpeed;

        // 경로 재계산은 "주기 경과 + 목표가 충분히 움직임" 둘 다 만족할 때만 (비용 절약)
        m_repathTimer += Time.deltaTime;
        if (
            m_repathTimer >= k_repathInterval
            && (target.position - m_lastTargetPos).sqrMagnitude
                >= k_repathMoveThreshold * k_repathMoveThreshold
        )
        {
            m_repathTimer = 0f;
            m_lastTargetPos = target.position;
            m_owner.Agent.SetDestination(target.position);
        }
    }

    public override void Exit()
    {
        // 연행 중 바꿨던 값들을 원복한다 — 근접 정지로 잠근 isStopped도 풀어
        // 다음 상태(배회 등)가 멈춘 채 시작되지 않게 한다 (#97)
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }
}
