using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 추격(Chasing) 상태 — 추격·재타겟·사냥·격퇴/수렴·기습 페이즈를 진행한다.
/// 조향·도달 판정·표적 선정·기습 계산은 Chase* 부품이 맡는다.
/// </summary>
public class NpcChaseState : NpcStateBase
{
    private const float k_convergeStopDistance = 1.6f;
    private const float k_catchRetrySeconds = 3f;

    private const float k_destinationSnapRadius = 2f;

    private readonly ChaseSteering m_steering;
    private readonly ChaseReachability m_reachability = new ChaseReachability();
    private readonly ChaseTargeting m_targeting;
    private readonly ChaseAmbush m_ambush;

    private readonly NavMeshPath m_pathBuffer = new NavMeshPath();

    private float m_baseSpeed;
    private float m_targetAcquiredTime;
    private float m_nextCatchNotifyTime;
    private float m_handledRepelUntil;
    private bool m_hunting;

    private readonly NpcChaseConfig m_config;
    private readonly NpcWalkConfig m_walkConfig;
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcChaseState(
        NpcController owner,
        NpcChaseConfig config,
        NpcWalkConfig walkConfig,
        NpcFleeConfig fleeConfig
    )
        : base(owner)
    {
        m_config = config;
        m_walkConfig = walkConfig;
        m_fleeConfig = fleeConfig;

        m_steering = new ChaseSteering(config);
        m_targeting = new ChaseTargeting(config, owner.Repath);
        m_ambush = new ChaseAmbush(config);
    }

    public override void Enter()
    {
        m_baseSpeed = m_owner.Agent.speed;
        m_targetAcquiredTime = Time.time;
        m_owner.Repath.ForceDue(NpcRepathChannel.Repath);
        m_nextCatchNotifyTime = 0f;
        m_hunting = false;

        m_steering.ClearLeadSample();
        m_reachability.Clear();
        m_targeting.Reset();

        m_steering.CaptureBaseline(m_owner.Agent);
        m_steering.Apply(m_owner.Agent, true);

        m_owner.Agent.updateRotation = false;

        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = 0f;
    }

    public override void Exit()
    {
        m_owner.Penalty.ClearDuty(NpcDutyKind.Pickpocket);

        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;

        m_steering.Apply(m_owner.Agent, false);
        m_owner.Agent.updateRotation = true;

        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.SetAgentStopped(false);
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        float now = Time.time;

        Transform converge = m_owner.Penalty.PenaltyConvergeTarget;
        if (converge != null)
        {
            TickConverge(converge);
            return;
        }

        if (now < m_owner.Penalty.ChaseRepelUntil)
        {
            TickRepelled(now);
            return;
        }

        Transform target = m_owner.Penalty.ChaseTarget;

        bool keepsTarget = m_owner.Penalty.IsUndercoverDuty;

        if (keepsTarget)
        {
            if (target == null)
            {
                TickHunt();
                return;
            }
        }
        else if (!m_targeting.IsChaseable(m_owner.transform.position, target, now))
        {
            target = m_targeting.PickNearest(m_owner.transform.position, now);
            m_owner.Penalty.SetChaseTarget(target);
            if (target != null)
                AcquireTarget(now, restartAccel: m_hunting);
        }

        if (target == null)
        {
            TickHunt();
            return;
        }

        if (m_owner.Penalty.IsAbductionDuty)
        {
            TickAmbush(target, now);
            return;
        }

        m_hunting = false;
        m_steering.Apply(m_owner.Agent, true);

        if (m_owner.Penalty.IsPickpocketDuty)
        {
            m_owner.Agent.speed = m_baseSpeed;
        }
        else
        {
            float elapsed = now - m_targetAcquiredTime;
            float accel = Mathf.Clamp01(elapsed / Mathf.Max(m_config.AccelSeconds, 0.01f));
            m_owner.Agent.speed = Mathf.Lerp(m_baseSpeed, m_config.MaxSpeed, accel);
        }

        m_owner.Agent.stoppingDistance = 0f;

        float distance = ChaseMath.FlatDistance(m_owner.transform.position, target.position);

        if (!keepsTarget)
        {
            Transform closer = m_targeting.FindCloser(
                m_owner.transform.position, target, distance, now);
            if (closer != null)
            {
                target = closer;
                m_owner.Penalty.SetChaseTarget(target);
                AcquireTarget(now, restartAccel: false);
                distance = ChaseMath.FlatDistance(m_owner.transform.position, target.position);
            }
        }

        if (m_owner.Repath.Due(NpcRepathChannel.Repath))
            SetChaseDestination(target, distance, now);

        m_steering.TickFacing(m_owner.Agent, m_owner.transform);

        if (m_reachability.IsUnreachable(now) && !keepsTarget)
        {
            m_targeting.PutOnCooldown(target, now);
            m_owner.Penalty.SetChaseTarget(null);
            m_reachability.Clear();
            m_steering.ClearLeadSample();
            TickHunt();
            return;
        }

        if (distance <= m_config.CatchDistance && now >= m_nextCatchNotifyTime)
        {
            m_nextCatchNotifyTime = now + k_catchRetrySeconds;
            m_owner.Penalty.NotifyPenaltyCaught(target);
        }
    }

    /// <summary>새 표적을 잡은 직후 도달 불가 누적을 초기화한다. restartAccel이면 가속도 다시 시작한다.</summary>
    private void AcquireTarget(float now, bool restartAccel)
    {
        if (restartAccel)
            m_targetAcquiredTime = now;

        m_reachability.Clear();
    }

    /// <summary>리드 조준 지점을 목적지로 잡고, 부분 경로면 표적 실제 위치나 NavMesh 스냅 지점으로 물러난다.</summary>
    private void SetChaseDestination(Transform target, float distance, float now)
    {
        Vector3 aim = m_steering.PredictAimPoint(target, distance, m_owner.Agent.speed, now);

        bool complete =
            NavMesh.CalculatePath(m_owner.transform.position, aim, m_owner.Agent.areaMask, m_pathBuffer)
            && m_pathBuffer.status == NavMeshPathStatus.PathComplete;

        if (!complete)
        {
            aim = target.position;

            if (
                NavMesh.SamplePosition(
                    aim,
                    out NavMeshHit hit,
                    k_destinationSnapRadius,
                    m_owner.Agent.areaMask
                )
            )
                aim = hit.position;
        }

        m_owner.Agent.SetDestination(aim);
        m_reachability.Report(!complete, now);
    }

    /// <summary>납치 기습 페이즈 — 표적 뒤로 걸어가 후방 각도 안에 닿으면 포획을 통보한다.</summary>
    private void TickAmbush(Transform target, float now)
    {
        m_hunting = false;
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        m_steering.Apply(m_owner.Agent, false);

        Vector3 position = m_owner.transform.position;
        float distance = ChaseMath.FlatDistance(position, target.position);
        bool behind = m_ambush.IsBehind(target, position);

        if (m_owner.Repath.Due(NpcRepathChannel.Repath))
            SetAmbushDestination(m_ambush.ApproachPoint(target, position));

        m_steering.TickFacing(m_owner.Agent, m_owner.transform);

        if (behind && distance <= m_config.CatchDistance && now >= m_nextCatchNotifyTime)
        {
            m_nextCatchNotifyTime = now + k_catchRetrySeconds;
            m_owner.Penalty.NotifyPenaltyCaught(target);
        }
    }

    private void SetAmbushDestination(Vector3 aim)
    {
        if (NavMesh.SamplePosition(aim, out NavMeshHit hit, k_destinationSnapRadius, m_owner.Agent.areaMask))
            aim = hit.position;

        m_owner.Agent.SetDestination(aim);
    }

    private void TickConverge(Transform converge)
    {
        m_owner.Agent.speed = m_config.MaxSpeed;
        m_owner.Agent.stoppingDistance = k_convergeStopDistance;
        m_steering.Apply(m_owner.Agent, false);

        if (m_owner.Repath.Due(NpcRepathChannel.Repath))
            m_owner.Agent.SetDestination(converge.position);

        m_steering.TickFacing(m_owner.Agent, m_owner.transform);
    }

    private void TickRepelled(float now)
    {
        if (m_handledRepelUntil != m_owner.Penalty.ChaseRepelUntil)
        {
            m_handledRepelUntil = m_owner.Penalty.ChaseRepelUntil;
            m_targeting.PutOnCooldown(m_owner.Penalty.ChaseRepelBy, now);
            m_owner.Penalty.SetChaseTarget(null);
        }

        m_owner.Agent.speed = m_config.MaxSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        m_steering.Apply(m_owner.Agent, false);
        m_steering.TickFacing(m_owner.Agent, m_owner.transform);

        if (m_owner.Penalty.ChaseRepelBy == null || !m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

        Vector3 away = (m_owner.transform.position - m_owner.Penalty.ChaseRepelBy.position).normalized;
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

    private void TickHunt()
    {
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.stoppingDistance = 0f;
        m_steering.Apply(m_owner.Agent, false);
        m_steering.TickFacing(m_owner.Agent, m_owner.transform);

        if (!m_hunting)
        {
            m_hunting = true;
            PickWanderPoint();
            return;
        }

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
                NpcNavAreas.ExcludeRoad(m_owner.Agent.areaMask)
            )
        )
            m_owner.Agent.SetDestination(hit.position);
    }
}
