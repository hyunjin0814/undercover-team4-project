using UnityEngine;

/// <summary>
/// 수감(Jailed) 상태 — 배정된 배치 지점에 서 있는다. (GDD 7-2, #228/#462/#492/#537)
///
/// <b>걷기가 사라졌다</b> (#537). 감옥이 도시에서 분리된 격리 공간이 되면서 진입이 순간이동으로
/// 바뀌었고(<see cref="JailIntake"/>), 예전의 "놓인 자리에서 좌석까지 1.5~5.6m를 걸어가 앉는다"는
/// 구간과 그 구간이 실패할 때 쓰던 안전망(경로 실패·경로 상실·20초 타임아웃 → 좌석으로 워프)이
/// 통째로 필요 없어졌다 — 실패할 이동이 없다.
///
/// <b>좌석도 폐기됐다</b> (#537). 앉기 모션과 착석 플래그가 사라지고 배치 지점에 <b>서 있는다</b>.
///
/// 그래서 진입이 하는 일은 셋뿐이다: 이동을 끊고, 지점 위로 옮기고, 지점이 보는 방향으로 돌린다.
/// 뒤의 둘은 <b>밖에서 들어올 때만</b> 한다 — 이미 방 안이면 건널 섬이 없으므로 그 자리에 선다.
/// 그 뒤로는 <see cref="Tick"/>이 방 안 배회를 돌린다.
///
/// 진입 경로는 <see cref="JailIntake"/>다 — 문 앞 E로 판정을 통과하면 그 순간 여기로 온다.
/// 빠져나가는 경로는 둘: 탈옥 방출(<see cref="JailbreakEvent"/>)과 플레이어의 반출(JailIntake.ServerExtract).
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 감옥 방 범위가 없는 씬(단독 테스트)에서만 쓰는 폴백 반경(m) — 배치 지점 둘레.
    private const float k_fallbackRadius = 1.6f;

    // 도착 판정 여유(m) — stoppingDistance에 더해 쓴다. 딱 맞추려 들면 미세하게 떨며 멈추지 못한다.
    private const float k_arriveSlack = 0.15f;

    // 다음 목적지를 고르기까지 서 있는 시간(초) 범위 — 계속 걷기만 하면 우리를 도는 로봇처럼 보인다.
    private const float k_pauseSecondsMin = 1.5f;
    private const float k_pauseSecondsMax = 5f;

    // 다음에 움직일 시각. 서 있는 동안만 의미가 있다.
    private float m_nextMoveTime;

    // <b>걷는 중인가 — 에이전트에게 묻지 않는다.</b>
    //
    // NavMeshAgent.isStopped는 경로가 없으면 무조건 false를 돌려준다. 그런데 쉬는 중이 곧 경로를
    // 지운 상태라(BeginPause → StopMoving → ResetPath), isStopped로 물으면 "쉬는 중"과
    // "걷다가 막 도착함"이 구분되지 않는다. 그러면 Tick이 매 프레임 도착 갈래로 빠져
    // BeginPause가 다시 걸리고, 다음 이동 시각이 끝없이 뒤로 밀려 <b>배회가 영영 착수되지 않는다</b>.
    private bool m_walking;

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        // 잠깐 선 채로 시작한다 — 배치되자마자 걷기 시작하면 순간이동한 자리에서 곧바로
        // 미끄러지는 그림이 된다. 지난 수감에서 남은 m_nextMoveTime도 여기서 씻긴다.
        BeginPause();

        if (m_owner.Custody.JailSpot == null)
            return; // 감옥이 배선되지 않은 테스트 씬 — 그 자리에 세운 것으로 처리한다

        // <b>이미 방 안이면 옮기지 않는다.</b> 아래 워프는 경로가 없는 두 NavMesh 섬을 건너는 수단이지
        // 자리를 정돈하는 수단이 아니다. 이 가드가 없으면 반출을 되돌릴 때
        // (JailIntake.ServerReturnToJail — 감옥 안에서 추종을 멈춘 순간) 플레이어 옆에 세워 둔 대상이
        // 배치 지점으로 빨려 들어간다. 방 안에서 다시 세운 것은 <b>그 자리에서</b> 수감돼야 한다.
        //
        // 방향도 맞추지 않는다 — 옮기지 않았으므로 지점이 보는 쪽으로 돌릴 이유가 없다.
        // 배치 지점(JailSpot)은 그대로 쥔 채다: 정원 계산과 배회 폴백의 기준으로 계속 쓰인다.
        if (JailRoom.Contains(m_owner.transform.position))
            return;

        // Warp = 위치를 즉시 옮기고 NavMesh에 다시 붙이는 것. 대상이 어디에 있었든(문 앞·도시 한복판)
        // 감옥 안 배치 지점으로 건너오는 유일한 수단이다 — 두 NavMesh 섬 사이에 경로가 없기 때문이다.
        //
        // 반환값을 보는 이유는 실패가 조용하기 때문이다: 지점이 NavMesh 밖이면 대상이
        // <b>문 앞에 그대로 남는데</b>, 눈으로는 "수감이 안 됐네"로만 보여 씬 배치 실수를 놓치기 쉽다.
        if (m_owner.Agent.Warp(m_owner.Custody.JailSpot.position))
        {
            m_owner.transform.rotation = SpotRotation();
            return;
        }

        // <b>에이전트가 꺼져 있으면 실패가 정상이다</b> — Warp는 그때 false를 돌려주지만 몸은 옮겨
        // 준다(실측). 들어오는 경로는 <b>기절 래그돌인 채로 수감되는 신병</b>이다: 검거는 무력화가
        // 전제라 거의 모든 수감이 이쪽이고, 밧줄을 걷어도 래그돌이 에이전트를 쥐고 있어 켜지지 않는다
        // (<c>NpcRopeDrag.ReleaseDrag</c>의 래그돌 가드 — "뗀 쪽이 되돌린다"). NavMesh 재부착은
        // 일어날 때 래그돌이 하고(<c>NpcRagdoll</c>), <see cref="Tick"/>은 붙기 전까지 배회를 미룬다.
        //
        // 방향은 맞추지 않는다 — 래그돌이 쥔 몸의 루트 회전은 골반을 따라가므로 여기서 돌려도 되돌아온다.
        if (!m_owner.Agent.enabled)
            return;

        Debug.LogWarning(
            $"NpcJailedState: 배치 지점으로 워프 실패 — 감옥 밖에 남는다. "
                + $"지점이 감옥 NavMesh 위에 있는지 확인할 것: {m_owner.Custody.JailSpot.name}",
            m_owner
        );
    }

    /// <summary>
    /// 감옥 안 배회 — 배치 지점 둘레를 어슬렁거린다. 서버(또는 오프라인)에서만 실제로 움직이고,
    /// 클라이언트는 NetworkTransform이 실어다 주는 결과만 본다.
    ///
    /// <b>가둬 둔 사람도 살아 있어야 한다</b> — 배치 지점에 못 박아 두면 마네킹으로 보이고,
    /// 본부 CCTV로 감옥을 볼 때 화면이 정지 화면과 구분되지 않는다.
    ///
    /// 목적지는 <b>방 전체</b>에서 고른다 (<see cref="JailRoom.TryRandomPoint"/>) — 자기 배치 지점
    /// 둘레만 맴돌면 갇혀 있다기보다 자리를 지키는 것처럼 보인다. 서로 비켜 가는 것은 에이전트의
    /// 회피에 맡긴다(수감 상태는 회피를 끄지 않는다).
    /// </summary>
    public override void Tick()
    {
        if (m_owner.Custody.JailSpot == null || !m_owner.Agent.isOnNavMesh)
            return;

        // 일어나는 중에는 움직이지 않는다 — 기상 클립이 도는 동안 걷기 시작하면 누운 몸이 미끄러진다
        if (m_owner.StandUp.IsStandingUp)
            return;

        // 걷는 중 — 도착했는지만 본다
        if (m_walking)
        {
            if (m_owner.Agent.pathPending)
                return;

            if (m_owner.Agent.remainingDistance > m_owner.Agent.stoppingDistance + k_arriveSlack)
                return;

            BeginPause();
            return;
        }

        // 쉬는 중 — 시간이 되면 다음 목적지를 고른다
        if (Time.time < m_nextMoveTime)
            return;

        BeginWander();
    }

    public override void Exit()
    {
        // 탈옥·반출로 풀려날 경우를 대비해 이동을 복구한다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 다음 목적지를 골라 걷기 시작한다. 못 고르면 그냥 더 쉰다.
    //
    // <b>방 전체를 쓴다</b> — 자기 배치 지점 둘레만 맴돌면 갇혀 있다기보다 자리를 지키는 것처럼 보인다.
    // 서로 비켜 가는 것은 에이전트의 회피에 맡긴다(수감 상태는 회피를 끄지 않는다).
    // 방을 못 찾는 테스트 씬에서는 배치 지점 둘레로 물러선다.
    private void BeginWander()
    {
        if (!JailRoom.TryRandomPoint(m_owner.Agent.areaMask, out Vector3 target)
            && !TrySpotNeighbourhood(out target))
        {
            BeginPause();
            return;
        }

        m_owner.Agent.isStopped = false;
        if (!m_owner.Agent.SetDestination(target))
        {
            BeginPause(); // 경로를 못 잡았다 — 다음 차례에 다시 고른다
            return;
        }

        m_walking = true;
    }

    // 폴백 — 감옥 방 범위가 없는 씬에서 배치 지점 둘레를 쓴다.
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

    // 잠시 선다 — 계속 걷기만 하면 우리 안을 도는 로봇처럼 보인다.
    private void BeginPause()
    {
        StopMoving();
        m_nextMoveTime = Time.time + Random.Range(k_pauseSecondsMin, k_pauseSecondsMax);
    }

    // 이동을 끊는다 — 끌려오던 관성이 남아 배치 지점에서 밀려나지 않게 속도까지 지운다.
    private void StopMoving()
    {
        m_walking = false;
        m_owner.Agent.velocity = Vector3.zero;
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.ResetPath();
        }
    }

    // 서서 바라볼 방향 — 지점 forward의 수평 성분만 쓴다(지점이 기울어 배치돼도 몸은 안 기운다).
    private Quaternion SpotRotation()
    {
        Vector3 forward = m_owner.Custody.JailSpot.forward;
        forward.y = 0f;
        return forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward)
            : m_owner.transform.rotation;
    }
}
