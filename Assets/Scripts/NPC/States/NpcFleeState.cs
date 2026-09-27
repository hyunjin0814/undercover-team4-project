using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 도주(Run) 상태 — 주변 플레이어 전원을 피해 먼 지점을 골라 도착할 때까지 달아난다(GDD 6-1).
/// 충분히 멀어지면 배회로 복귀하고, 완전히 포위되면 저항으로 전환한다.
/// </summary>
public class NpcFleeState : NpcStateBase
{
    private const float k_arriveThreshold = 0.5f;

    private const int k_directionSampleCount = 16;

    private const float k_navSampleMaxDistance = 2f;

    private const float k_farNavSampleMaxDistance = 4f;

    private const float k_pathClearanceMinT = 0.05f;

    private const float k_stuckMinProgress = 0.5f;

    private const int k_stuckStrikesToRepick = 2;

    private const int k_maxStuckRepicks = 2;

    private const int k_maxReachabilityProbes = 4;

    private static readonly List<Transform> s_threatBuffer = new List<Transform>(8);

    private static readonly List<FleeCandidate> s_candidates = new List<FleeCandidate>(k_directionSampleCount);

    private static NavMeshPath s_pathProbe;

    private struct FleeCandidate
    {
        public Vector3 Point;
        public float Score;
    }

    private float m_baseSpeed;
    private Vector3 m_lastProgressPosition;
    private int m_stuckStrikes;
    private int m_stuckRepicks;
    private float m_fleeStartTime;
    private bool m_transitioningToResist;

    private readonly NpcFleeConfig m_config;

    public NpcFleeState(NpcController owner, NpcFleeConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_owner.SetAgentStopped(false);
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_config.SpeedMultiplier;

        ResetStuck();
        m_owner.Repath.MarkDone(NpcRepathChannel.ThreatScan);

        m_fleeStartTime = Time.time;
        m_transitioningToResist = false;

        SetFleePoint();
    }

    public override void Tick()
    {
        bool arrived =
            !m_owner.Agent.pathPending
            && m_owner.Agent.remainingDistance
                <= m_owner.Agent.stoppingDistance + k_arriveThreshold;

        if (arrived)
        {
            ResetStuck();
            SetFleePoint();

            if (m_transitioningToResist)
                return;
        }
        else if (TickStuckWatch())
        {
            return;
        }

        if (!m_owner.Repath.Due(NpcRepathChannel.ThreatScan))
            return;

        CollectThreats(m_config.EscapeDistance);
        Transform nearest = NearestThreat(m_owner.transform.position);

        if (nearest == null)
        {
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        if (m_owner.Reaction.ThreatTarget == null)
            m_owner.Reaction.StartFlee(nearest);
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        if (!m_transitioningToResist)
            m_owner.Reaction.ClearThreat();
    }

    /// <summary>360도를 훑어 플레이어에게서 가장 먼 도주 지점을 고른다. 후보가 없으면 저항으로 전환한다.</summary>
    private void SetFleePoint()
    {
        Vector3 origin = m_owner.transform.position;

        CollectThreats(m_owner.Reaction.ThreatSearchRadius);
        if (s_threatBuffer.Count == 0)
        {
            if (m_owner.Reaction.ThreatTarget != null)
                s_threatBuffer.Add(m_owner.Reaction.ThreatTarget);
            else
                return;
        }

        float clearanceSqr = m_config.ClearanceRadius * m_config.ClearanceRadius;

        s_candidates.Clear();
        Vector3 fallbackPoint = Vector3.zero;
        float fallbackClearance = float.NegativeInfinity;
        bool hasFallback = false;

        for (int i = 0; i < k_directionSampleCount; i++)
        {
            float angle = 360f / k_directionSampleCount * i;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

            if (
                !TrySamplePoint(
                    origin,
                    direction,
                    m_config.FarPointDistance,
                    k_farNavSampleMaxDistance,
                    m_owner.Agent.areaMask,
                    out Vector3 point
                )
                && !TrySamplePoint(
                    origin,
                    direction,
                    m_config.StepDistance,
                    k_navSampleMaxDistance,
                    m_owner.Agent.areaMask,
                    out point
                )
            )
                continue;
            float pathClearanceSqr = float.MaxValue;
            float arrivalNearestSqr = float.MaxValue;

            foreach (Transform threat in s_threatBuffer)
            {
                Vector3 threatPos = threat.position;
                arrivalNearestSqr = Mathf.Min(arrivalNearestSqr, (threatPos - point).sqrMagnitude);

                float t;
                float segSqr = SqrDistanceToSegment(threatPos, origin, point, out t);
                if (t > k_pathClearanceMinT)
                    pathClearanceSqr = Mathf.Min(pathClearanceSqr, segSqr);
            }

            if (pathClearanceSqr < clearanceSqr)
            {
                if (pathClearanceSqr > fallbackClearance)
                {
                    fallbackClearance = pathClearanceSqr;
                    fallbackPoint = point;
                    hasFallback = true;
                }
                continue;
            }

            s_candidates.Add(new FleeCandidate { Point = point, Score = arrivalNearestSqr });
        }

        s_candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        int probes = Mathf.Min(k_maxReachabilityProbes, s_candidates.Count);
        for (int i = 0; i < probes; i++)
        {
            if (!IsReachable(origin, s_candidates[i].Point))
                continue;

            m_owner.Agent.SetDestination(s_candidates[i].Point);
            return;
        }

        if (s_candidates.Count > probes)
        {
            m_owner.Agent.SetDestination(s_candidates[probes].Point);
            return;
        }

        if (Time.time - m_fleeStartTime < m_config.ResistCooldown)
        {
            if (hasFallback)
                m_owner.Agent.SetDestination(fallbackPoint);
            return;
        }

        TransitionToResist("포위");
    }

    /// <summary>도주를 접고 저항으로 — 도주로가 막혔다는 결론이 같으므로 포위·길막이 같은 경로를 쓴다.</summary>
    private void TransitionToResist(string reason)
    {
        Debug.Log($"도주로 차단({reason}) — 저항 전환: {m_owner.name}");
        m_transitioningToResist = true;
        m_owner.Reaction.StartResist(m_owner.Reaction.ThreatTarget);
    }

    /// <summary>실제 이동 거리로 막힘을 감시해 도주 지점을 재추첨하고, 안 풀리면 저항으로 전환해 true를 돌려준다.</summary>
    private bool TickStuckWatch()
    {
        if (m_owner.Stun.IsStunned)
        {
            ResetStuck();
            return false;
        }

        if (!m_owner.Repath.Due(NpcRepathChannel.StuckCheck))
            return false;

        Vector3 position = m_owner.transform.position;
        float progress = Vector3.Distance(position, m_lastProgressPosition);
        m_lastProgressPosition = position;

        if (progress >= k_stuckMinProgress)
        {
            m_stuckStrikes = 0;
            m_stuckRepicks = 0;
            return false;
        }

        m_stuckStrikes++;
        if (m_stuckStrikes < k_stuckStrikesToRepick)
            return false;
        m_stuckStrikes = 0;
        m_stuckRepicks++;

        if (
            m_stuckRepicks >= k_maxStuckRepicks
            && Time.time - m_fleeStartTime >= m_config.ResistCooldown
        )
        {
            TransitionToResist("길막");
            return true;
        }

        Debug.Log(
            $"도주 막힘 — {progress:F2}m/{m_owner.Repath.IntervalOf(NpcRepathChannel.StuckCheck)}s, 지점 재추첨 {m_stuckRepicks}회: {m_owner.name}"
        );
        SetFleePoint();

        return m_transitioningToResist;
    }

    private void ResetStuck()
    {
        m_owner.Repath.MarkDone(NpcRepathChannel.StuckCheck);
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;
        m_stuckRepicks = 0;
    }

    /// <summary>origin에서 point까지 끊기지 않는 경로가 있는가 — 부분 경로는 도달 불가로 본다.</summary>
    private bool IsReachable(Vector3 origin, Vector3 point)
    {
        s_pathProbe ??= new NavMeshPath();

        return NavMesh.CalculatePath(origin, point, m_owner.Agent.areaMask, s_pathProbe)
            && s_pathProbe.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>origin에서 direction으로 distance 간 지점을 areaMask 내 NavMesh에 샘플한다. 실패하면 false.</summary>
    private static bool TrySamplePoint(
        Vector3 origin,
        Vector3 direction,
        float distance,
        float sampleMaxDistance,
        int areaMask,
        out Vector3 point
    )
    {
        point = default;
        if (
            !NavMesh.SamplePosition(
                origin + direction * distance,
                out NavMeshHit hit,
                sampleMaxDistance,
                areaMask
            )
        )
            return false;

        point = hit.position;
        return true;
    }

    /// <summary>반경 내 행동 가능한 플레이어를 공유 버퍼에 모은다.</summary>
    private void CollectThreats(float radius)
    {
        SuddenEventUtil.CollectFieldPlayers(m_owner.transform.position, radius, s_threatBuffer);
    }

    /// <summary>공유 버퍼에서 기준점에 가장 가까운 위협 — 비어 있으면 null.</summary>
    private static Transform NearestThreat(Vector3 origin)
    {
        Transform nearest = null;
        float nearestSqr = float.MaxValue;
        foreach (Transform threat in s_threatBuffer)
        {
            float sqr = (threat.position - origin).sqrMagnitude;
            if (sqr < nearestSqr)
            {
                nearestSqr = sqr;
                nearest = threat;
            }
        }
        return nearest;
    }

    /// <summary>점과 선분(a→b) 사이 수평 최단거리의 제곱과 최근접 위치 t를 구한다.</summary>
    private static float SqrDistanceToSegment(Vector3 point, Vector3 a, Vector3 b, out float t)
    {
        Vector2 p = new Vector2(point.x, point.z);
        Vector2 start = new Vector2(a.x, a.z);
        Vector2 end = new Vector2(b.x, b.z);

        Vector2 segment = end - start;
        float sqrLength = segment.sqrMagnitude;
        if (sqrLength < Mathf.Epsilon)
        {
            t = 0f;
            return (p - start).sqrMagnitude;
        }

        t = Mathf.Clamp01(Vector2.Dot(p - start, segment) / sqrLength);
        Vector2 closest = start + segment * t;
        return (p - closest).sqrMagnitude;
    }
}
