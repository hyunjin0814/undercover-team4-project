using UnityEngine;
using UnityEngine.AI;

public partial class NpcController
{
    // ---- 넉백 (#232) ----

    /// <summary>폭발 등으로 날아가는 중인가 — 이 동안 FSM·NavMesh는 멈춘다. 서버(또는 오프라인)에서만 유효.</summary>
    public bool IsKnockedBack => m_knockbackActive;

    /// <summary>
    /// 외력으로 날려보낸다 — 폭발 넉백(<see cref="BombDevice"/>) 등. 세기는 m/s 단위 초기 속도로 준다.
    ///
    /// <b>서버(또는 오프라인) 전용.</b> 플레이어 넉백은 각 피어가 자기 오너 캐릭터에 적용하지만
    /// (<see cref="PlayerMovement.AddKnockback"/>), NPC는 이동 권한이 서버의 NavMeshAgent에 있고
    /// 클라이언트는 NetworkTransform으로 결과만 받으므로 서버가 직접 민다.
    ///
    /// 수감(<see cref="NpcState.Jailed"/>)·침입(<see cref="NpcState.Intruding"/>)은 제외한다 —
    /// 이벤트가 그 NPC의 진행(수용·자물쇠 해제)을 쥐고 있어서, 중간에 날아가면 판정 경로가 끊긴다.
    /// 체포·연행 중인 NPC는 <b>수갑을 찬 채</b> 날아가고 착지 후에도 체포 상태로 남는다
    /// (연행만 풀린다 — <see cref="PlayerEscorter"/>가 Escorted 이탈을 보고 스스로 참조를 정리한다).
    /// </summary>
    public void ServerApplyKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_knockbackActive)
            return; // 같은 폭발이 콜라이더 여러 개로 잡힌 중복 호출 — 처음 것만 받는다
        if (velocity.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_stateMachine.CurrentState; // 서버 진실값 — 동기화 지연 없이 판정
        if (state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        // 스턴 오버레이와 겹치면 넉백이 이긴다 (#292). 그냥 두면 Update의 스턴 게이트가
        // 착지 후 NpcStunnedState.Tick을 가로채 깨어나지 못한다 — 게이트 순서상 오버레이가
        // FSM보다 앞이기 때문이다. 기절 시간은 넉백 기준으로 새로 흐른다.
        ClearStunOverlay();

        // 상태 전이가 먼저다 — 에이전트를 끄기 전에 넣어야 상태 클래스가 에이전트를 정상적으로 정리한다.
        // 비행 중에는 FSM Tick을 건너뛰므로 상태별 타이머(기절 해제·인계 방치)는 착지 후부터 흐른다.
        m_knockbackLandingState = state == NpcState.Captured || state == NpcState.Escorted
            ? NpcState.Captured // 검거 유지 — 폭발로 수갑이 풀리지는 않는다
            : NpcState.Stunned; // 그 외엔 축 늘어져 날아가 기절 상태로 착지한다

        if (state == NpcState.Escorted)
            StopEscort(); // 연행만 해제(Captured 전이) — 에이전트 정리는 Escorted.Exit이 맡는다
        else
            m_stateMachine.ChangeState(m_knockbackLandingState);

        m_knockbackActive = true;
        m_knockbackStranded = false; // 복구 대기 중에 다시 맞았다 — 새 비행이 우선이다 (#423)
        m_knockbackVelocity = velocity;
        m_knockbackLaunch = transform.position;
        m_knockbackElapsed = 0f;

        // 에이전트가 켜져 있으면 매 프레임 NavMesh 위로 끌어내려 애초에 뜨지 못한다
        if (m_agent.enabled)
        {
            if (m_agent.isOnNavMesh)
                m_agent.ResetPath();
            m_agent.enabled = false;
        }
    }

    // 포물선 비행 1프레임. 착지하면 NavMesh 위로 되돌리고 발사 시점에 정한 상태로 넘긴다.
    private void TickKnockback()
    {
        m_knockbackElapsed += Time.deltaTime;
        m_knockbackVelocity.y += m_commonConfig.KnockbackGravity * Time.deltaTime;

        Vector3 next = transform.position + m_knockbackVelocity * Time.deltaTime;

        // 벽을 뚫고 날아가지 않게 실제 콜라이더를 훑는다 — 지금 위치에서 이번 프레임 수평 이동분만큼
        // 몸통 굵기로 스윕한다.
        //
        // NavMesh를 충돌 프록시로 쓰면 안 된다: NavMesh는 실제 벽보다 에이전트 반지름만큼 물러나 끝나고
        // 연석·차도 경계에서도 끊긴다. 실측(Test Scene)에서 벽이 11.8m 밖인 방향이 NavMesh 기준으로는
        // 2.0m에서 "막힘"으로 나왔고, 그걸 벽으로 치면 수평 속도가 비행 첫 프레임에 0이 되어
        // 넉백이 그대로 제자리 점프가 된다 (#232).
        Vector3 horizontalStep = new Vector3(next.x - transform.position.x, 0f, next.z - transform.position.z);
        float stepDistance = horizontalStep.magnitude;
        if (stepDistance > 0.0001f && SweepHitsObstacle(horizontalStep / stepDistance, stepDistance, out _))
        {
            // 진짜 벽에 닿았다 — 수평 성분을 버리고 그 자리에서 떨어진다
            next.x = transform.position.x;
            next.z = transform.position.z;
            m_knockbackVelocity.x = 0f;
            m_knockbackVelocity.z = 0f;
        }

        transform.position = next;

        bool timedOut = m_knockbackElapsed >= m_commonConfig.KnockbackMaxFlightSeconds;
        if (!timedOut && m_knockbackVelocity.y > 0f)
            return; // 아직 상승 중 — 착지 판정은 내려올 때부터

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit ground, m_commonConfig.KnockbackLandSampleDistance, NavMesh.AllAreas))
        {
            if (!timedOut && transform.position.y > ground.position.y + 0.05f)
                return; // 아직 공중

            EndKnockback(ground.position);
            return;
        }

        // NavMesh를 아예 벗어난 곳까지 날아갔다 — 시간이 다 되면 출발점으로 회수한다(맵 밖 유실 방지)
        if (timedOut)
            EndKnockback(m_knockbackLaunch);
    }

    // 벽 스윕 히트 버퍼 — 넉백 틱은 서버 전용이라 공유해도 안전하다 (프레임마다의 할당 방지)
    private static readonly RaycastHit[] s_sweepBuffer = new RaycastHit[16];

    // 이번 프레임 수평 이동 구간에 벽이 있는지 — 몸통 굵기로 훑는다.
    // 프레임이 튀어 한 번에 몇 미터씩 움직여도 구간 전체를 검사하므로 벽을 지나쳐 버리지 않는다.
    // 캐릭터(플레이어·다른 NPC)는 벽으로 치지 않는다(#339): 플레이어 몸통이 환경과 같은 Default 레이어라
    // 마스크만으로는 걸러지지 않는데, 폭발로 날아가는 NPC가 군중을 벽으로 오판하면 죄다 제자리에
    // 툭 떨어져 넉백이 밋밋해진다 — 장애물 스윕 판정(#313)과 같은 수정.
    private bool SweepHitsObstacle(Vector3 direction, float distance, out RaycastHit obstacle)
    {
        obstacle = default;

        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_commonConfig.KnockbackObstacleMask & ~(1 << gameObject.layer); // 자기 콜라이더에 걸리지 않게

        int count = Physics.SphereCastNonAlloc(origin, radius, direction, s_sweepBuffer, distance, mask,
                                               QueryTriggerInteraction.Ignore);

        // 버퍼 포화 = 반환되지 못한 히트(그중 진짜 벽 포함 가능)가 있을 수 있다 — 나오면 확대 신호 (#313 리뷰와 동일)
        if (count == s_sweepBuffer.Length)
            Debug.LogWarning($"NpcController: 스윕 버퍼 포화({count}) — 히트 누락 가능", this);

        bool found = false;
        for (int i = 0; i < count; i++)
        {
            Collider hit = s_sweepBuffer[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerData>() != null)
                continue; // 플레이어 — 벽이 아니다, 뚫고 날아간다
            if (hit.GetComponentInParent<NpcController>() != null)
                continue; // 다른 NPC — 군중 속 폭발에서 서로를 벽으로 보지 않게

            // 시작 지점에서 이미 겹친 히트(거리 0)는 버린다 — 법선이 진행 방향 반대로 잡혀 어느 쪽으로
            // 움직여도 계속 막히므로, 한 번 끼면 영영 빠져나오지 못한다 (밧줄 끌기에서 실제로 낀 사례, #369).
            // 이미 안에 있는 이상 막는 것보다 빠져나갈 기회를 주는 편이 항상 낫다.
            if (s_sweepBuffer[i].distance <= 0.001f)
                continue;

            // 캐릭터가 아닌 무언가 = 벽/환경. 여럿이면 가장 가까운 것을 남긴다.
            if (!found || s_sweepBuffer[i].distance < obstacle.distance)
            {
                obstacle = s_sweepBuffer[i];
                found = true;
            }
        }

        return found;
    }

    private void EndKnockback(Vector3 landing)
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;

        m_agent.enabled = true;
        m_agent.Warp(landing); // 에이전트를 NavMesh 위 착지점에 다시 붙인다

        // 착지점을 NavMesh에 붙이지 못했다(건물 위 등) — 복구 대기로 넘긴다 (#423).
        // 예전에는 경고만 남기고 포기해서, 에이전트가 켜진 채 NavMesh 밖에 남았다 —
        // FSM이 곧바로 재개되어 상태 클래스가 매 프레임 isStopped/SetDestination을 부르며 에러를 쏟았다.
        if (!m_agent.isOnNavMesh)
        {
            BeginKnockbackStranded();
            return;
        }

        // 이미 발사 시점에 전이해 뒀다 — 여기 호출은 그 사이 상태가 바뀐 경우를 위한 보정이다.
        // (같은 상태면 StateMachine이 무시하므로 상태별 타이머가 착지 시점에 리셋되지도 않는다)
        m_stateMachine.ChangeState(m_knockbackLandingState);
    }

    // ---- NavMesh 밖 착지 복구 (#423) ----

    // 착지 실패 — 에이전트를 도로 끄고 복구 대기로 들어간다.
    // 끄지 않으면 넉백 게이트가 풀린 직후 상태 클래스가 NavMesh 밖 에이전트를 건드린다.
    private void BeginKnockbackStranded()
    {
        m_agent.enabled = false;
        m_knockbackStranded = true;
        m_knockbackStrandedElapsed = 0f;

        Debug.LogWarning(
            $"NpcController: 넉백 착지 지점을 NavMesh에 붙이지 못했다 — {m_commonConfig.KnockbackStrandedRecoverySeconds}초 뒤 복귀시킨다: {name}",
            this);
    }

    // 복구 대기 1프레임 — 유예 시간이 지나면 NavMesh 위로 되돌린다. FSM 대신 Update가 부른다.
    private void TickKnockbackRecovery()
    {
        m_knockbackStrandedElapsed += Time.deltaTime;
        if (m_knockbackStrandedElapsed < m_commonConfig.KnockbackStrandedRecoverySeconds)
            return;

        // 떨어진 자리 주변(건물 위라면 그 아래 지상)을 먼저 보고, 없으면 날아오기 전 자리로 되돌린다.
        if (!TryRecoverToNavMesh(transform.position) && !TryRecoverToNavMesh(m_knockbackLaunch))
        {
            m_knockbackStrandedElapsed = 0f; // 둘 다 실패 — 유예 시간만큼 쉬었다 다시 시도한다
            return;
        }

        m_knockbackStranded = false;

        // 발사 시점에 정해 둔 상태로 마저 넘긴다 — 정상 착지했을 때와 같은 처리다.
        m_stateMachine.ChangeState(m_knockbackLandingState);
    }

    // 기준점 주변 NavMesh로 에이전트를 되돌린다 — 붙었으면 true. 실패하면 에이전트를 도로 꺼 둔다.
    // 탐색 반경은 착지 판정(KnockbackLandSampleDistance)보다 넓다 — 건물 위에서 지상까지 닿아야 한다.
    // 영역 마스크는 에이전트 것을 그대로 쓴다 — AllAreas로 찾으면 통행이 금지된 영역으로 되돌아갈 수 있다.
    private bool TryRecoverToNavMesh(Vector3 origin)
    {
        if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, m_commonConfig.KnockbackRecoverySampleDistance, m_agent.areaMask))
            return false;

        m_agent.enabled = true;
        if (m_agent.Warp(hit.position) && m_agent.isOnNavMesh)
            return true;

        m_agent.enabled = false; // 다음 시도를 위해 원래대로 꺼 둔다
        return false;
    }
}
