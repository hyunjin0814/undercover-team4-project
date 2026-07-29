using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 오검거 추격(Chasing) 상태 — 원한 구역에서 출동한 시민이 플레이어를 쫓는다. (#278)
/// 한 상태 안에서 네 페이즈를 다룬다:
///
/// - <b>추격</b>: 타겟을 향해 가속하며 쫓는다. 최고 속도는 플레이어 전력질주보다 낮아(ChaseMaxSpeed)
///   직선에서는 계속 달리면 벗어날 수 있다 — 대신 범위 이탈 시 아래 재타겟으로 페널티가 전가된다.
/// - <b>재타겟</b>: 타겟이 추격 범위(ChaseRange)를 벗어나거나 무력화되면, 범위 안의 플레이어 중
///   무작위 한 명으로 갈아탄다(잡히는 사람이 페널티 독박 — 부모 이슈 #276 확정 설계).
/// - <b>사냥</b>: 범위 안에 아무도 없으면 배회하며 범위에 들어오는 플레이어를 기다린다.
/// - <b>격퇴/수렴</b>: 격퇴(ApplyChaseRepel, 호루라기 #250 예정)당하면 잠시 도주 후 사냥으로 복귀하고
///   그 플레이어에게 재추격 쿨다운을 건다. 누군가 포획되면(PenaltyConvergeTarget) 전원 그리로 모인다.
///
/// 포획은 거리 판정 — WrongfulArrestPenalty가 OnPenaltyCaught를 구독해 수렴·호송(#279)을 지휘한다.
/// </summary>
public class NpcChaseState : NpcStateBase
{
    private const float k_repathInterval = 0.2f; // 경로 재계산 최소 간격(초) — NpcEscortedState와 동일
    private const float k_scanInterval = 0.5f; // 사냥 모드에서 범위 내 플레이어를 훑는 주기(초)
    private const float k_convergeStopDistance = 1.6f; // 수렴 시 포획된 플레이어 앞 정지 거리(m)
    private const float k_catchRetrySeconds = 3f; // 포획 통보 재시도 간격 — 매니저가 다른 호송 중이라 무시해도 스팸이 안 되게

    private readonly List<Transform> m_candidateBuffer = new List<Transform>();

    // 격퇴당한 플레이어별 재추격 금지 종료 시각(Time.time). 상태 인스턴스는 NPC마다 1개라 NPC별 기록이 된다.
    private readonly Dictionary<Transform, float> m_targetCooldowns =
        new Dictionary<Transform, float>();

    private float m_baseSpeed; // 진입 전 원래 속도 — 사냥 모드 속도이자 Exit 복원값
    private float m_targetAcquiredTime; // 현재 타겟 확보 시각 — 가속 기준점 (타겟이 바뀌면 리셋)
    private float m_repathTimer;
    private float m_scanTimer;
    private float m_nextCatchNotifyTime;
    private float m_handledRepelUntil; // 이미 쿨다운을 등록한 격퇴인지 — 같은 격퇴에 중복 등록 방지
    private bool m_hunting; // 사냥(배회) 모드 중인지 — 추격/사냥 간 속도·목적지 전환용

    private readonly NpcChaseConfig m_config;
    private readonly NpcWalkConfig m_walkConfig;
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcChaseState(NpcController owner, NpcChaseConfig config, NpcWalkConfig walkConfig, NpcFleeConfig fleeConfig)
        : base(owner)
    {
        m_config = config;
        m_walkConfig = walkConfig;
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_targetAcquiredTime = Time.time;
        m_repathTimer = 0f;
        m_scanTimer = 0f;
        m_nextCatchNotifyTime = 0f;
        m_hunting = false;

        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f; // 수렴 페이즈가 올린 정지 거리 원복 — 배회 복귀 시 목적지 앞 멈춤 방지
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        m_repathTimer -= Time.deltaTime;

        // ---- 수렴: 포획 확정 — 전원 포획된 플레이어에게 모인다. 추격·격퇴보다 우선한다 (#279)
        Transform converge = m_owner.PenaltyConvergeTarget;
        if (converge != null)
        {
            TickConverge(converge);
            return;
        }

        // ---- 격퇴: 호루라기(#250 예정)에 쫓겨나 잠시 도주 — 유예 창. 끝나면 사냥/재타겟으로 이어진다
        if (Time.time < m_owner.ChaseRepelUntil)
        {
            TickRepelled();
            return;
        }

        // ---- 타겟 유효성: 사라짐·무력화·범위 이탈·쿨다운이면 범위 안 무작위 플레이어로 갈아탄다
        Transform target = m_owner.ChaseTarget;
        if (!IsChaseable(target))
        {
            target = PickRandomTargetInRange();
            m_owner.SetChaseTarget(target);
            if (target != null)
                m_targetAcquiredTime = Time.time; // 새 타겟 — 가속을 처음부터 다시 밟는다
        }

        // ---- 사냥: 범위 안에 아무도 없다 — 배회하며 기다린다 (걷는 속도)
        if (target == null)
        {
            TickHunt();
            return;
        }

        // ---- 추격: 가속하며 쫓고, 붙으면 포획을 통보한다
        m_hunting = false;
        float elapsed = Time.time - m_targetAcquiredTime;
        float accel = Mathf.Clamp01(elapsed / Mathf.Max(m_config.AccelSeconds, 0.01f));
        m_owner.Agent.speed = Mathf.Lerp(m_baseSpeed, m_config.MaxSpeed, accel);
        m_owner.Agent.stoppingDistance = 0f;

        if (m_repathTimer <= 0f)
        {
            m_repathTimer = k_repathInterval;
            m_owner.Agent.SetDestination(target.position);
        }

        float distance = Vector3.Distance(m_owner.transform.position, target.position);
        if (distance <= m_config.CatchDistance && Time.time >= m_nextCatchNotifyTime)
        {
            // 매니저가 이미 다른 호송을 처리 중이면 통보가 무시된다 — 재시도 간격을 두고 계속 붙어 다닌다
            m_nextCatchNotifyTime = Time.time + k_catchRetrySeconds;
            m_owner.NotifyPenaltyCaught(target);
        }
    }

    // 포획된 플레이어에게 모여 선다 — 도착 판정·호송 개시는 매니저(WrongfulArrestPenalty)가 거리로 지휘한다.
    private void TickConverge(Transform converge)
    {
        m_owner.Agent.speed = m_config.MaxSpeed; // 수렴은 전속 — 연출 대기를 줄인다
        m_owner.Agent.stoppingDistance = k_convergeStopDistance;

        if (m_repathTimer <= 0f)
        {
            m_repathTimer = k_repathInterval;
            m_owner.Agent.SetDestination(converge.position);
        }
    }

    // 격퇴 도주 — 격퇴한 플레이어 반대 방향으로 달아난다. 같은 격퇴당 한 번만 재추격 쿨다운을 등록한다.
    private void TickRepelled()
    {
        if (m_handledRepelUntil != m_owner.ChaseRepelUntil)
        {
            m_handledRepelUntil = m_owner.ChaseRepelUntil;
            if (m_owner.ChaseRepelBy != null)
                m_targetCooldowns[m_owner.ChaseRepelBy] = Time.time + m_config.RetargetCooldown;
            m_owner.SetChaseTarget(null); // 도주가 끝나면 재타겟부터 다시 — 쿨다운 대상은 후보에서 빠진다
        }

        m_owner.Agent.speed = m_config.MaxSpeed;
        m_owner.Agent.stoppingDistance = 0f;

        if (m_repathTimer > 0f || m_owner.ChaseRepelBy == null)
            return;

        m_repathTimer = k_repathInterval;
        Vector3 away = (m_owner.transform.position - m_owner.ChaseRepelBy.position).normalized;
        if (away.sqrMagnitude < 0.01f)
            away = m_owner.transform.forward;

        Vector3 candidate = m_owner.transform.position + away * m_fleeConfig.StepDistance;
        if (
            NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                m_fleeConfig.StepDistance,
                m_owner.Agent.areaMask
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }

    // 사냥 모드 — 걷는 속도로 배회하며 범위에 들어오는 플레이어를 기다린다 (재타겟은 Tick 상단이 주기 스캔으로 처리).
    private void TickHunt()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;

        if (!m_hunting)
        {
            m_hunting = true;
            PickWanderPoint();
            return;
        }

        // 배회 지점 도착 — 다음 지점을 뽑는다
        if (!m_owner.Agent.pathPending && m_owner.Agent.remainingDistance < 0.6f)
            PickWanderPoint();
    }

    private void PickWanderPoint()
    {
        Vector2 dir = Random.insideUnitCircle.normalized;
        float dist = Random.Range(m_walkConfig.MinWanderDistance, m_walkConfig.WanderRadius);
        Vector3 candidate = m_owner.transform.position + new Vector3(dir.x, 0f, dir.y) * dist;
        if (
            NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                m_walkConfig.WanderRadius,
                m_owner.Agent.areaMask
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }

    // 현재 타겟을 계속 쫓아도 되는가 — 존재·행동 가능·추격 범위 안·재추격 쿨다운 아님.
    private bool IsChaseable(Transform target)
    {
        if (target == null)
            return false;
        if (IsOnCooldown(target))
            return false;

        PlayerData data = target.GetComponent<PlayerData>();
        if (data == null || !data.IsTargetable)
            return false;

        return Vector3.Distance(m_owner.transform.position, target.position) <= m_config.Range;
    }

    // 추격 범위 안의 행동 가능한 플레이어 중 무작위 — 쿨다운 대상 제외. 없으면 null(사냥 모드).
    // 주기 스캔(k_scanInterval)으로 스로틀한다 — 사냥 중 매 프레임 전 플레이어 순회 방지.
    private Transform PickRandomTargetInRange()
    {
        if (m_scanTimer > Time.time)
            return null;
        m_scanTimer = Time.time + k_scanInterval;

        SuddenEventUtil.CollectFieldPlayers(
            m_owner.transform.position,
            m_config.Range,
            m_candidateBuffer
        );
        for (int i = m_candidateBuffer.Count - 1; i >= 0; i--)
        {
            if (IsOnCooldown(m_candidateBuffer[i]))
                m_candidateBuffer.RemoveAt(i);
        }

        if (m_candidateBuffer.Count == 0)
            return null;

        return m_candidateBuffer[Random.Range(0, m_candidateBuffer.Count)];
    }

    private bool IsOnCooldown(Transform target) =>
        m_targetCooldowns.TryGetValue(target, out float until) && Time.time < until;
}
