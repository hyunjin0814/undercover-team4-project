using UnityEngine;

/// <summary>
/// 체포(Captured) 상태 — 밧줄 끌기를 놓거나(E) 도주 제압 완료 시 진입한다. (GDD 7-4, 이슈 #36/#369)
/// 이동·배회를 완전히 멈춘다.
///
/// 인계 방치 타이머를 든다 (GDD 7-6, #230): 이 상태로 <see cref="NpcCapturedConfig.EscapeSeconds"/>가
/// 지나도록 인계되지 않으면 밧줄을 풀고 도주한다 — "일단 다 잡아놓고 나중에 인계" 전략 차단.
/// 판정이 끝난(<see cref="NpcController.IsDelivered"/>) NPC는 제외한다 — 본부에서 탈출하면 안 되고,
/// 그 뒤 처리는 유치장(#228) 몫이다. 예외가 하나 있다: 유치장 밖으로 반출해 놓고 방치한 대상은
/// 판정이 끝났어도 달아난다 (<see cref="IsAbandonedAfterExtraction"/>, #517).
///
/// <b>밧줄에 묶인 채 이 상태면 누워 있다</b> (#513) — E 놓기는 끌기만 멈추고 줄은 그대로다.
/// 그래서 방치 만료는 "일어나기 → 도주" 2단이고, 그 사이가 재포획 창이다 (<see cref="Escape"/>).
/// </summary>
public class NpcCapturedState : NpcStateBase
{
    // 도주 예정 시각(Time.time 기준). Enter마다 다시 잡히므로 "Captured 진입할 때마다 리셋"이 공짜로 성립한다 —
    // 타이머를 미루려면 직접 걸어와 재연행(E)해야 하니 꼼수 가치가 낮다.
    private float m_escapeTime;

    private readonly NpcCapturedConfig m_config;

    public NpcCapturedState(NpcController owner, NpcCapturedConfig config)
        : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        // 이동을 멈추고 진행 중이던 배회 경로도 제거한다
        m_owner.Agent.isStopped = true;
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();

        m_escapeTime = Time.time + m_config.EscapeSeconds;
    }

    public override void Tick()
    {
        // 판정 완료 = 인계 성공. 본부에 얌전히 남는다 (#230)
        if (m_owner.IsDelivered && !IsAbandonedAfterExtraction)
            return;

        // 이미 일어나는 중 — 끝나면 도주로 이어진다. 그 사이 다시 묶이면 예약이 취소되고
        // 커스터디 재진입(Enter)이 타이머를 새로 잡는다. (#513)
        if (m_owner.IsStandingUp)
            return;

        float remaining = m_escapeTime - Time.time;

        if (remaining <= 0f)
        {
            Escape();
            return;
        }
    }

    public override void Exit()
    {
        // 향후 이송·석방 등으로 풀릴 경우를 대비해 이동을 복구한다
        m_owner.Agent.isStopped = false;
    }

    /// <summary>
    /// 유치장 밖으로 반출해 놓고 방치한 대상인가 — 판정 완료(<see cref="NpcController.IsDelivered"/>)
    /// 제외의 예외다. (#517, 팀 확정 2026-08-04)
    ///
    /// 반출된 대상은 이미 정산·진행도에서 빠져 있어(JailIntake.ServerExtract) 그냥 두면 팀 손실만 남긴 채
    /// 영원히 서 있는다 — 방치 타이머까지 빠져 있으면 "놓친 신병"이 아니라 그냥 배경이 된다.
    /// 방치하면 달아나게 해서 <b>다시 잡아야 하는 상황</b>으로 만든다.
    ///
    /// <b>유치장 안은 제외한다.</b> 안에서 달아나게 두면 "시민은 유치장에 못 들어간다"(#415)가 없애려던
    /// 그림 — 유치장 안을 배회하는 NPC — 가 되살아난다. 안에 선 대상은 아직 회수 가능한 신병이기도 하다.
    /// 위치를 보는 근거는 PlayerEscortCommands의 풀기 분기와 같다: JailArea는 "이 좌표가 Jail 영역인가"를
    /// 답하는 정적 판정 유틸이라, 이걸 읽는 것이 유치장을 아는 것은 아니다.
    /// </summary>
    private bool IsAbandonedAfterExtraction =>
        m_owner.IsJailExtracted && !JailArea.Contains(m_owner.transform.position);

    /// <summary>방치 타이머 만료 — 일어난 뒤 풀려나 달아난다. (#513)
    ///
    /// 묶인 대상은 누워 있으므로(#513) 만료 순간 곧바로 도주하면 누운 몸이 그대로 미끄러진다.
    /// 일어나기 모션을 먼저 태우고 그 길이만큼 지난 뒤 달아난다 — <b>그 구간이 곧 재포획 창</b>이다.
    /// 아직 묶인 채 <see cref="NpcState.Captured"/>이므로 달려가 E를 누르면 끌기가 재개되고
    /// (<see cref="NpcController.StartRopeDrag"/>가 예약을 취소한다) 자세도 누운 상태로 되돌아간다.
    /// "일어난다 = 곧 달아난다"가 지금은 없던 예고 신호가 된다.
    ///
    /// 이미 서 있는 대상(제압만으로 잡혀 묶인 적 없는 Captured)은 기다리지 않고 곧바로 달아난다 —
    /// 그 판정은 <see cref="NpcController.ServerStandUpThen"/>이 한다.</summary>
    private void Escape()
    {
        m_owner.ServerStandUpThen(Flee);
    }

    /// <summary>일어난 뒤 실제로 달아난다.</summary>
    private void Flee()
    {
        // 밧줄은 소모형이 아니라 반환할 자원이 없다 — 상태 전이만으로 풀려난다. (#369)

        // 가장 가까운 플레이어를 위협 삼아 도주한다 — 반경은 저항 폴백(#205)·도주 회피(#213)와 같은
        // ThreatSearchRadius를 쓴다. 기준이 어긋나면 "도망칠 상대"와 "피할 상대"가 달라진다.
        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position,
            m_owner.ThreatSearchRadius
        );

        if (nearest != null)
        {
            Debug.Log($"인계 방치 — 풀려나 도주: {m_owner.name}");
            m_owner.StartFlee(nearest.transform);
            return;
        }

        // 근처에 아무도 없으면 도망칠 이유도 없다 — 조용히 풀려나 배회로 복귀(사실상 탈출).
        // NpcResistState.Defeat의 폴백과 같은 패턴.
        Debug.Log($"인계 방치 — 풀려나 배회 복귀(주변에 플레이어 없음): {m_owner.name}");
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}
