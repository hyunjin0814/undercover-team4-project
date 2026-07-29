using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 도주(Run) 상태 — 수갑 채널링 성공 순간 뿌리치고 추격하는 플레이어들에게서 달아난다. (GDD 6-1, #76)
/// 추적자와 충분히 멀어지면 도주 성공으로 보고 배회로 복귀한다.
/// 잡는 방법: 근접 제압 홀드(NpcSubdueInteractable) 또는 테이저(후속 아이템).
///
/// 도주 방향은 위협 1명이 아니라 <see cref="NpcController.ThreatSearchRadius"/> 안의 플레이어 전원을 보고 고른다 (#213) —
/// 협공하면 두 사람 사이로 뛰어드는 대신 옆으로 빠지고, 완전히 포위되면 저항으로 전환한다.
///
/// 도주 지점은 멀리(<see cref="NpcFleeConfig.FarPointDistance"/>) 잡고 <b>도착할 때까지 커밋</b>한다 —
/// 추격자가 움직일 때마다 실시간으로 방향을 다시 재지 않는다(팀 피드백: 갈지자 없이 먼 곳으로 쭉 도주).
/// </summary>
public class NpcFleeState : NpcStateBase
{
    private const float k_arriveThreshold = 0.5f;

    // 후보 방향 개수 — 360도를 이 수로 등분해 훑는다. 16개면 22.5도 간격이라
    // 좌우 협공의 정답인 수직 방향도, 1:1 추격의 정답인 정반대 방향도 후보에 들어온다.
    private const int k_directionSampleCount = 16;

    // 후보 도착점을 NavMesh 위로 끌어당길 때 허용하는 최대 거리(m)
    private const float k_navSampleMaxDistance = 2f;

    // 먼 지점(FarPointDistance) 샘플의 허용 거리(m) — 멀수록 건물 안 등에 떨어질 확률이 높아 여유를 더 준다
    private const float k_farNavSampleMaxDistance = 4f;

    // 경로(origin→point) 위에서 위협의 최근접점이 이 t(정규화 위치)보다 앞(interior)에 있을 때만
    // clearance 제약을 건다. t가 0에 가까우면 최근접점이 origin이라 그 위협을 '등지고' 뛰는 방향이므로,
    // 이미 위협에 붙어 있어 origin이 clearance 안이더라도 그 방향까지 막지 않는다 (#213 오판 수정).
    private const float k_pathClearanceMinT = 0.05f;

    // 막힘 감지 — 플레이어가 몸으로 길을 막으면(#400) 진행이 멈추는데, 도주 지점은 도착까지
    // 커밋이라 방향을 다시 뽑는 경로가 없어 제자리 달리기로 굳는다.
    // Agent.velocity로는 못 잡는다 — 로컬 회피가 장애물 표면을 따라 좌우로 미끄러져 속도가 0으로
    // 떨어지지 않기 때문이다(NPC Rigidbody는 kinematic이라 물리로 멈추는 것도 아니다).
    private const float k_stuckCheckInterval = 0.5f;

    // 한 구간에 이 거리(m)도 못 갔으면 막힘 — 도주 속도 6m/s면 0.5초에 3m는 간다
    private const float k_stuckMinProgress = 0.5f;

    // 막힘이 이 횟수(= 1초) 이어지면 도주 지점을 재추첨한다
    private const int k_stuckStrikesToRepick = 2;

    // 재추첨을 이 횟수만큼 해도 계속 제자리면 저항으로 전환한다 — 좁은 골목·문턱을 막은 경우엔
    // 막힌 방향이 유일한 통로라 같은 방향이 계속 뽑히고, 그건 포위와 다를 게 없다.
    private const int k_maxStuckRepicks = 2;

    // 이탈 판정(위협 스캔)의 최소 간격(초) — 씬 전체 검색이라 매 프레임 돌리지 않는다.
    // 도주 지점 재계산에는 쓰지 않는다 — 지점은 도착까지 커밋한다 (팀 피드백, 구 #96 실시간 재계산 제거)
    private const float k_scanInterval = 0.25f;

    // 서버에서만 Tick되므로 버퍼 공유 안전 — 매 재계산마다의 할당 방지 (NpcResistState와 같은 방식)
    private static readonly List<Transform> s_threatBuffer = new List<Transform>(8);

    private float m_baseSpeed;
    private float m_scanTimer;
    private float m_stuckCheckTimer;
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
        m_owner.Agent.isStopped = false;
        m_baseSpeed = m_owner.Agent.speed;
        m_owner.Agent.speed = m_baseSpeed * m_config.SpeedMultiplier;

        m_scanTimer = 0f;
        ResetStuck();
        m_fleeStartTime = Time.time;
        m_transitioningToResist = false;

        SetFleePoint();
    }

    public override void Tick()
    {
        m_scanTimer += Time.deltaTime;

        // 도주 지점 도착 판정 — Agent 내부 값만 읽으므로 매 프레임 확인해도 공짜다 (기존 동작)
        bool arrived =
            !m_owner.Agent.pathPending
            && m_owner.Agent.remainingDistance
                <= m_owner.Agent.stoppingDistance + k_arriveThreshold;

        // 도착했을 때만 다음 지점을 잡는다 — 지점은 커밋이라 도중에 방향을 다시 재지 않는다 (팀 피드백)
        if (arrived)
        {
            ResetStuck();
            SetFleePoint();

            // SetFleePoint가 포위로 보고 저항으로 넘겼을 수 있다 — 그대로 진행하면 아래 이탈 판정이
            // 추격자를 못 찾은 프레임에 Idle로 덮어써 방금 건 저항이 사라진다.
            if (m_transitioningToResist)
                return;
        }
        else if (TickStuckWatch())
        {
            return; // 길막·포위로 저항 전환됨 — 이 상태는 끝났다
        }

        // 위협 스캔(CollectThreats)은 씬 전체 FindObjectsByType이라 매 프레임 돌리면
        // 도주 중인 NPC 수만큼 비용이 누적된다(범인 다수 + 미끼 시민 + 난동꾼) — 주기로 묶는다.
        // 이탈 판정이 최대 k_scanInterval만큼 늦어지지만 게임 상 차이는 없다.
        if (m_scanTimer < k_scanInterval)
            return;
        m_scanTimer = 0f;

        // 이탈 판정은 도주 방향 산출과 반경이 다르다 — 방향은 근처(ThreatSearchRadius) 플레이어만 보면 되지만,
        // 이탈은 FleeEscapeDistance(25m)까지 아무도 없어야 성립한다.
        CollectThreats(m_config.EscapeDistance);
        Transform nearest = NearestThreat(m_owner.transform.position);

        if (nearest == null)
        {
            // 추격권 안에 아무도 없다 — 위협이 사라졌든 다 따돌렸든 도망갈 이유가 없다
            m_owner.StateMachine.ChangeState(NpcState.Idle);
            return;
        }

        // 위협 대상이 사라졌으면(연결 종료 등) 가장 가까운 추격자로 폴백한다 —
        // 도주형에는 이 폴백이 없어 Idle로 빠지던 문제 (#213). NpcResistState의 폴백(#205)과 대칭.
        if (m_owner.ThreatTarget == null)
            m_owner.StartFlee(nearest);
    }

    public override void Exit()
    {
        m_owner.Agent.speed = m_baseSpeed;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        // 포위로 저항 전환하는 경우엔 위협을 남긴다 — StartResist가 세팅한 위협을 그 직후 이 Exit가
        // 지워버리면 저항이 유발자를 잃는다. NpcResistState가 ClearThreat를 Exit이 아니라
        // 체포 시점에 두는 것과 같은 이유. (#205, #213)
        if (!m_transitioningToResist)
            m_owner.ClearThreat();
    }

    /// <summary>
    /// 360도를 훑어 도주 지점을 고른다 — 경로가 플레이어를 스치는 방향은 버리고,
    /// 남은 후보 중 도착점에서 가장 가까운 플레이어까지의 거리가 최대인 쪽을 택한다.
    /// 지점은 멀리(FarPointDistance) 잡고 도착할 때까지 커밋한다 — 막힌 방향만 StepDistance로 줄여 잡는다.
    /// 남는 후보가 없으면 포위가 성립한 것이므로 저항으로 전환한다.
    /// </summary>
    private void SetFleePoint()
    {
        Vector3 origin = m_owner.transform.position;

        CollectThreats(m_owner.ThreatSearchRadius);
        if (s_threatBuffer.Count == 0)
        {
            // 회피 반경 안에 아무도 없다 — 추격자는 멀리 있으니(이탈 판정은 Tick이 담당) 아무 방향으로나 계속 뛴다
            if (m_owner.ThreatTarget != null)
                s_threatBuffer.Add(m_owner.ThreatTarget);
            else
                return;
        }

        float clearanceSqr = m_config.ClearanceRadius * m_config.ClearanceRadius;

        Vector3 bestPoint = Vector3.zero;
        float bestScore = float.NegativeInfinity; // 필터를 통과한 후보의 도착점 maximin
        Vector3 fallbackPoint = Vector3.zero;
        float fallbackClearance = float.NegativeInfinity; // 전부 탈락했을 때를 위한 '그나마 나은' 후보
        bool hasBest = false;
        bool hasFallback = false;

        for (int i = 0; i < k_directionSampleCount; i++)
        {
            // 월드 축 기준 등간격 스윕 — away 벡터를 기준축으로 삼지 않으므로
            // 좌우 대칭 협공에서 방향이 0벡터로 상쇄되는 문제가 아예 생기지 않는다
            float angle = 360f / k_directionSampleCount * i;
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

            // 먼 지점 우선(커밋 도주) — 건물 안 등으로 샘플이 실패한 방향은 가까운 지점으로 줄여 다시 시도
            if (
                !TrySamplePoint(origin, direction, m_config.FarPointDistance,
                    k_farNavSampleMaxDistance, m_owner.Agent.areaMask, out Vector3 point)
                && !TrySamplePoint(origin, direction, m_config.StepDistance,
                    k_navSampleMaxDistance, m_owner.Agent.areaMask, out point)
            )
                continue; // 어느 거리로도 갈 수 없는 방향
            float pathClearanceSqr = float.MaxValue; // 경로가 플레이어를 스치는 최단거리
            float arrivalNearestSqr = float.MaxValue; // 도착점에서 가장 가까운 플레이어까지 거리

            foreach (Transform threat in s_threatBuffer)
            {
                Vector3 threatPos = threat.position;
                arrivalNearestSqr = Mathf.Min(arrivalNearestSqr, (threatPos - point).sqrMagnitude);

                // 위협이 경로의 시작점(origin) 쪽(t≈0)에 있으면 이 방향은 그 위협을 등지고 뛰는 방향이다.
                // origin 근처라는 이유만으로 모든 방향을 탈락시키면(위협이 4m 안으로 붙는 순간)
                // 앞이 뻥 뚫려 있어도 포위로 오판한다 — 앞(interior)에 있는 위협만 clearance 제약에 넣는다.
                float t;
                float segSqr = SqrDistanceToSegment(threatPos, origin, point, out t);
                if (t > k_pathClearanceMinT)
                    pathClearanceSqr = Mathf.Min(pathClearanceSqr, segSqr);
            }

            if (pathClearanceSqr < clearanceSqr)
            {
                // 탈락 — 이 방향으로 가면 도중에 잡힌다. 다만 전부 탈락할 경우를 대비해 최선을 기억해 둔다.
                if (pathClearanceSqr > fallbackClearance)
                {
                    fallbackClearance = pathClearanceSqr;
                    fallbackPoint = point;
                    hasFallback = true;
                }
                continue;
            }

            if (arrivalNearestSqr > bestScore)
            {
                bestScore = arrivalNearestSqr;
                bestPoint = point;
                hasBest = true;
            }
        }

        if (hasBest)
        {
            m_owner.Agent.SetDestination(bestPoint);
            return;
        }

        // 통과 후보 0개 = 포위 성립. 다만 저항에서 막 넘어온 직후라면(#205 저항 승리 → 도주 전환)
        // 즉시 되돌아가 프레임마다 왕복하므로, 쿨다운 동안은 그나마 나은 방향으로 뚫고 나가려 시도한다. (#213)
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
        m_owner.StartResist(m_owner.ThreatTarget);
    }

    /// <summary>
    /// 막힘 감시 — 일정 간격으로 실제 이동 거리를 보고, 제자리면 도주 지점을 재추첨한다.
    /// 재추첨으로도 안 풀리면 저항으로 전환하고 true를 반환한다(이 상태는 끝났다는 뜻). (#400)
    /// </summary>
    private bool TickStuckWatch()
    {
        // 기절 중엔 멈춰 있는 게 정상이다 — 스턴은 상태가 아니라 플래그라(#292) 여기서 세면
        // 테이저 한 방에 저항으로 돌변한다.
        if (m_owner.IsStunned)
        {
            ResetStuck();
            return false;
        }

        m_stuckCheckTimer += Time.deltaTime;
        if (m_stuckCheckTimer < k_stuckCheckInterval)
            return false;
        m_stuckCheckTimer = 0f;

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

        // 저항 전환은 도주 진입 직후엔 막는다 — 저항에서 막 넘어온 경우 프레임마다 왕복하기
        // 때문이다 (SetFleePoint의 포위 판정과 같은 가드, #205/#213)
        if (m_stuckRepicks >= k_maxStuckRepicks
            && Time.time - m_fleeStartTime >= m_config.ResistCooldown)
        {
            TransitionToResist("길막");
            return true;
        }

        // 재추첨하면 도착점 maximin 점수가 막고 선 쪽 방향을 떨어뜨려 옆·뒤로 빠진다
        Debug.Log(
            $"도주 막힘 — {progress:F2}m/{k_stuckCheckInterval}s, 지점 재추첨 {m_stuckRepicks}회: {m_owner.name}");
        SetFleePoint();

        // 재추첨이 포위로 판단해 저항으로 넘어갔으면 그것도 호출부에 알려야 한다
        return m_transitioningToResist;
    }

    private void ResetStuck()
    {
        m_stuckCheckTimer = 0f;
        m_lastProgressPosition = m_owner.transform.position;
        m_stuckStrikes = 0;
        m_stuckRepicks = 0;
    }

    /// <summary>origin에서 direction으로 distance만큼 간 지점을 NavMesh 위로 샘플한다 — 실패 시 false.
    /// areaMask는 도주 주체의 통행 마스크 — 못 가는 영역(Jail)으로 도주 지점을 잡지 않게 한다 (#415).</summary>
    private static bool TrySamplePoint(
        Vector3 origin, Vector3 direction, float distance, float sampleMaxDistance, int areaMask,
        out Vector3 point)
    {
        point = default;
        if (!NavMesh.SamplePosition(
                origin + direction * distance, out NavMeshHit hit, sampleMaxDistance, areaMask))
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

    /// <summary>
    /// 점과 선분(a→b) 사이 최단거리의 제곱 — 수평면(XZ) 기준. 도주 경로가 플레이어를 스치는지 판정한다.
    /// <paramref name="t"/>는 최근접점의 선분 위 정규화 위치([0,1]) — 0이면 시작점(a), 1이면 끝점(b)이다.
    /// </summary>
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
            return (p - start).sqrMagnitude; // 선분이 점으로 뭉개진 경우
        }

        // 선분 위로의 정사영을 [0,1]로 잘라 최근접점을 구한다
        t = Mathf.Clamp01(Vector2.Dot(p - start, segment) / sqrLength);
        Vector2 closest = start + segment * t;
        return (p - closest).sqrMagnitude;
    }
}
