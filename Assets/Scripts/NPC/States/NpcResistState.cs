using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 저항(Attack) 상태 — 표적을 향해 주기적으로 정면 부채꼴 타격을 휘두른다(GDD 6-1/7-4).
/// 교전 플레이어가 전원 무력화되면 도주로, 표적이 없으면 일정 시간 후 배회로 돌아간다.
/// </summary>
public class NpcResistState : NpcStateBase
{
    private const int k_maxOverlapHits = 64;

    private const float k_losHeight = 1f;

    private static readonly Collider[] s_overlapBuffer = new Collider[k_maxOverlapHits];

    private static int s_hitLayers;

    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0;
            }
            return s_hitLayers;
        }
    }
    private static readonly List<PlayerHealth> s_playerBuffer = new List<PlayerHealth>(8);

    private static readonly List<PlayerHealth> s_launchedBuffer = new List<PlayerHealth>(6);

    private const float k_noPendingStrike = -1f;

    private const float k_stopDistanceFactor = 0.8f;
    private const float k_chaseRepathMoveThreshold = 0.5f;
    private static readonly Vector3 k_noDestination = new Vector3(float.PositiveInfinity, 0f, 0f);

    private float m_noTargetSeconds;
    private float m_nextAttackTime;
    private float m_pendingStrikeTime = k_noPendingStrike;

    private float m_swingHoldUntil;

    private Vector3 m_lastChaseDestination;
    private float m_baseSpeed;
    private float m_baseAcceleration;

    private readonly NavMeshPath m_pathBuffer = new NavMeshPath();

    private int m_roadArea = -1;
    private float m_baseRoadCost = 1f;

    private readonly NpcResistConfig m_config;
    private readonly NpcFleeConfig m_fleeConfig;

    public NpcResistState(NpcController owner, NpcResistConfig config, NpcFleeConfig fleeConfig) : base(owner)
    {
        m_config = config;
        m_fleeConfig = fleeConfig;
    }

    public override void Enter()
    {
        m_owner.SetAgentStopped(false);
        m_owner.Agent.stoppingDistance = m_config.AttackRange * k_stopDistanceFactor;

        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_fleeConfig.SpeedMultiplier;

        m_baseAcceleration = m_owner.Agent.acceleration;
        m_owner.Agent.acceleration = m_config.ChaseAcceleration;

        m_roadArea = m_owner.AgentReady ? NpcNavAreas.RoadArea : -1;
        if (m_roadArea >= 0)
        {
            m_baseRoadCost = m_owner.Agent.GetAreaCost(m_roadArea);
            m_owner.Agent.SetAreaCost(m_roadArea, 1f);
        }

        m_owner.Agent.updateRotation = false;

        m_noTargetSeconds = 0f;

        m_nextAttackTime = Time.time;

        m_pendingStrikeTime = k_noPendingStrike;
        m_swingHoldUntil = 0f;

        m_lastChaseDestination = k_noDestination;
    }

    public override void Tick()
    {
        Transform target = ResolveTarget();

        if (target == null)
        {
            m_noTargetSeconds += Time.deltaTime;
            if (m_noTargetSeconds >= m_config.NoTargetIdleSeconds)
            {
                Debug.Log($"저항 종료(표적 상실) — 배회 복귀: {m_owner.name}");
                m_owner.Reaction.ClearThreat();
                m_owner.StateMachine.ChangeState(NpcState.Idle);
                return;
            }
        }
        else
        {
            m_noTargetSeconds = 0f;
        }

        ChaseTarget(target);

        if (Time.time >= m_swingHoldUntil)
            FaceTarget(target);

        bool canStrike = target != null
            && (target.position - m_owner.transform.position).sqrMagnitude
                <= m_config.AttackRange * m_config.AttackRange
            && IsInFrontCone(target.position);
        if (canStrike && Time.time >= m_nextAttackTime)
        {
            m_nextAttackTime = Time.time + m_config.AttackInterval;
            int variant = Random.Range(0, m_config.SwingVariantCount);
            m_owner.Reaction.RaiseAttackSwing(variant);
            m_pendingStrikeTime = Time.time + m_config.SwingImpactOffset(variant);

            m_swingHoldUntil = Time.time + m_config.SwingHoldSeconds;
            if (m_owner.Agent.isOnNavMesh)
                m_owner.SetAgentStopped(true);
        }

        if (m_pendingStrikeTime >= 0f && Time.time >= m_pendingStrikeTime)
        {
            m_pendingStrikeTime = k_noPendingStrike;
            if (SwingAttack())
            {
                Defeat("교전 플레이어 전원 무력화");
                return;
            }
        }
    }

    public override void Exit()
    {
        m_owner.SetAgentStopped(false);
        m_owner.Agent.updateRotation = true;
        m_owner.Agent.stoppingDistance = 0f;
        m_owner.Agent.speed = m_baseSpeed;
        m_owner.Agent.acceleration = m_baseAcceleration;

        if (m_roadArea >= 0 && m_owner.AgentReady)
            m_owner.Agent.SetAreaCost(m_roadArea, m_baseRoadCost);
    }

    /// <summary>정면 부채꼴 사거리 안 플레이어를 타격한다. 교전한 플레이어가 전원 HP 0이면 true.</summary>
    private bool SwingAttack()
    {
        CollectPlayersInRange(m_config.AttackRange);

        int engaged = 0;
        int aliveCount = 0;
        int struck = 0;
        foreach (PlayerHealth player in s_playerBuffer)
        {
            if (!IsInFrontCone(player.transform.position))
                continue;

            if (!HasLineOfSight(player))
                continue;

            engaged++;
            if (player.CurrentHp <= 0)
                continue;

            ((IDamageable)player).TakeDamage(m_config.AttackDamage, m_owner.gameObject);
            struck++;
            if (player.CurrentHp > 0)
                aliveCount++;
        }

        if (struck > 0)
            m_owner.Reaction.RaiseAttackHit();

        if (engaged == 0)
            return false;

        Debug.Log($"저항 범위 타격: {m_owner.name} → 정면 {engaged}명 (잔존 {aliveCount}명)");
        return aliveCount == 0;
    }

    /// <summary>이번 틱의 표적을 정한다 — 유발자 우선, 없으면 반경 내 가장 가까운 현장 플레이어.</summary>
    private Transform ResolveTarget()
    {
        Transform threat = m_owner.Reaction.ThreatTarget;
        if (threat != null && IsStillEngaged(threat))
            return threat;

        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position, m_owner.Reaction.ThreatSearchRadius);
        return nearest != null ? nearest.transform : null;
    }

    /// <summary>유발자가 여전히 교전 가능한 표적인지(거리·다운 여부) 판정한다.</summary>
    private bool IsStillEngaged(Transform threat)
    {
        float giveUpSqr = m_config.GiveUpDistance * m_config.GiveUpDistance;
        if ((threat.position - m_owner.transform.position).sqrMagnitude > giveUpSqr)
            return false;

        PlayerHealth player = threat.GetComponentInParent<PlayerHealth>();
        return player == null || player.IsTargetable;
    }

    private const float k_directPathSlack = 1.5f;

    /// <summary>표적을 향해 이동한다. 직선 경로가 없으면 갈 수 있는 데까지만 다가간다.</summary>
    private void ChaseTarget(Transform target)
    {
        if (Time.time < m_swingHoldUntil)
            return;

        if (target == null)
        {
            if (m_owner.Agent.isOnNavMesh)
                m_owner.SetAgentStopped(true);
            return;
        }

        m_owner.SetAgentStopped(false);

        bool moved = (target.position - m_lastChaseDestination).sqrMagnitude
            >= k_chaseRepathMoveThreshold * k_chaseRepathMoveThreshold;
        if (!moved && !m_owner.Repath.Due(NpcRepathChannel.Repath))
            return;

        m_owner.Repath.MarkDone(NpcRepathChannel.Repath);
        m_lastChaseDestination = target.position;

        if (!m_owner.Agent.isOnNavMesh)
            return;

        if (IsDirectlyReachable(target.position))
        {
            m_owner.Agent.stoppingDistance = m_config.AttackRange * k_stopDistanceFactor;
            m_owner.Agent.SetDestination(target.position);
            return;
        }

        if (!TryClosestApproach(target.position, out Vector3 approach))
        {
            m_owner.Agent.ResetPath();
            return;
        }

        m_owner.Agent.stoppingDistance = 0f;
        m_owner.Agent.SetDestination(approach);
    }

    /// <summary>표적을 향한 직선이 NavMesh를 벗어나는 지점을 찾는다. 못 찾으면 false.</summary>
    private bool TryClosestApproach(Vector3 target, out Vector3 point)
    {
        if (NavMesh.Raycast(m_owner.transform.position, target, out NavMeshHit hit, m_owner.Agent.areaMask))
        {
            point = hit.position;
            return true;
        }

        Vector3[] corners = m_pathBuffer.corners;
        if (m_pathBuffer.status == NavMeshPathStatus.PathPartial && corners.Length > 0)
        {
            point = corners[corners.Length - 1];
            return true;
        }

        point = m_owner.transform.position;
        return false;
    }

    private bool IsDirectlyReachable(Vector3 destination)
    {
        float straight = Vector3.Distance(m_owner.transform.position, destination);
        if (straight < 0.01f)
            return true;

        if (!NavMesh.Raycast(m_owner.transform.position, destination, out NavMeshHit _, m_owner.Agent.areaMask))
            return true;

        if (
            !m_owner.Agent.CalculatePath(destination, m_pathBuffer)
            || m_pathBuffer.status != NavMeshPathStatus.PathComplete
        )
            return false;

        return PathLength(m_pathBuffer) <= straight * k_directPathSlack;
    }

    private static float PathLength(NavMeshPath path)
    {
        Vector3[] corners = path.corners;
        float length = 0f;
        for (int i = 1; i < corners.Length; i++)
            length += Vector3.Distance(corners[i - 1], corners[i]);
        return length;
    }

    private const float k_facingMoveSpeed = 0.5f;

    /// <summary>이동 중에는 이동 방향을, 멈추면 표적을 향해 몸을 돌린다. 서버(또는 오프라인) 전용.</summary>
    private void FaceTarget(Transform target)
    {
        if (target == null)
            return;

        Vector3 velocity = m_owner.Agent.velocity;
        velocity.y = 0f;

        Vector3 to;
        if (velocity.sqrMagnitude >= k_facingMoveSpeed * k_facingMoveSpeed)
        {
            to = velocity;
        }
        else
        {
            to = target.position - m_owner.transform.position;
            to.y = 0f;
        }

        if (to.sqrMagnitude < 0.0001f)
            return;

        Quaternion look = Quaternion.LookRotation(to);
        m_owner.transform.rotation = Quaternion.RotateTowards(
            m_owner.transform.rotation, look, m_config.AttackTurnSpeed * Time.deltaTime);
    }

    /// <summary>주어진 위치가 NPC 정면 부채꼴(AttackConeAngle) 안인지 — 수평 방향 기준.</summary>
    private bool IsInFrontCone(Vector3 position)
    {
        Vector3 to = position - m_owner.transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f)
            return true;

        Vector3 forward = m_owner.transform.forward;
        forward.y = 0f;
        return Vector3.Angle(forward, to) <= m_config.AttackConeAngle * 0.5f;
    }

    /// <summary>NPC에서 그 플레이어까지 벽에 안 막히는가. 맞은 것이 그 플레이어 자신이 아니면 막힌 것이다.</summary>
    private bool HasLineOfSight(PlayerHealth player)
    {
        Vector3 origin = m_owner.transform.position + Vector3.up * k_losHeight;
        Vector3 target = player.transform.position + Vector3.up * k_losHeight;
        Vector3 toTarget = target - origin;
        float distance = toTarget.magnitude;
        if (distance < 0.01f)
            return true;

        if (
            !Physics.Raycast(
                origin, toTarget / distance, out RaycastHit hit, distance, HitLayers,
                QueryTriggerInteraction.Ignore)
        )
            return true;

        return hit.collider.GetComponentInParent<PlayerHealth>() == player;
    }

    /// <summary>플레이어 승리 실패 — 저항을 유발한 플레이어(없으면 근처 플레이어)를 위협 삼아 도주형으로 전환한다.</summary>
    private void Defeat(string reason)
    {
        Debug.Log($"저항 승리({reason}) — 도주 전환: {m_owner.name}");

        Transform threat = m_owner.Reaction.ThreatTarget;
        if (threat == null)
        {
            PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
                m_owner.transform.position,
                m_owner.Reaction.ThreatSearchRadius
            );
            threat = nearest != null ? nearest.transform : null;
        }

        if (threat != null)
            m_owner.Reaction.StartFlee(threat);
        else
            m_owner.StateMachine.ChangeState(NpcState.Idle);
    }

    /// <summary>반경 내 PlayerHealth를 래그돌 본을 제외하고 중복 없이 모은다.</summary>
    private void CollectPlayersInRange(float radius)
    {
        s_playerBuffer.Clear();
        int hitCount = Physics.OverlapSphereNonAlloc(
            m_owner.transform.position, radius, s_overlapBuffer, HitLayers);

        if (hitCount == s_overlapBuffer.Length)
            Debug.LogWarning($"저항 타격 판정 버퍼가 찼다 — 뒤로 밀린 대상이 잘렸을 수 있다: {m_owner.name}");

        for (int i = 0; i < hitCount; i++)
        {
            PlayerHealth player = s_overlapBuffer[i].GetComponentInParent<PlayerHealth>();
            if (player != null && !s_playerBuffer.Contains(player))
                s_playerBuffer.Add(player);
        }

        PlayerHealth.CollectLaunched(m_owner.transform.position, radius, s_launchedBuffer);
        for (int i = 0; i < s_launchedBuffer.Count; i++)
        {
            if (!s_playerBuffer.Contains(s_launchedBuffer[i]))
                s_playerBuffer.Add(s_launchedBuffer[i]);
        }
    }
}
