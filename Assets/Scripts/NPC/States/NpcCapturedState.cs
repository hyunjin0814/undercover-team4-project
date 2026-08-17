using UnityEngine;

/// <summary>
/// 체포(Captured) 상태 — 밧줄 끌기를 놓거나(E) 도주 제압 완료 시 진입한다. (GDD 7-4, 이슈 #36/#369)
/// 이동·배회를 완전히 멈춘다.
///
/// 인계 방치 타이머를 든다 (GDD 7-6, #230): 이 상태로 <see cref="NpcCapturedConfig.EscapeSeconds"/>가
/// 지나도록 인계되지 않으면 밧줄을 풀고 도주한다 — "일단 다 잡아놓고 나중에 인계" 전략 차단.
/// 판정이 끝난(<see cref="NpcCustody.IsDelivered"/>) NPC와 유치장 안에 있는 NPC는 제외한다 —
/// 잠긴 유치장·본부에서 탈출하면 안 되고, 그 뒤 처리는 유치장(#228) 몫이다. 예외가 하나 있다:
/// 유치장 밖으로 반출해 놓고 방치한 대상은 판정이 끝났어도 달아난다 (#517).
/// 판정은 <see cref="StaysPut"/> — 밧줄 끊김(PlayerEscorter)과 공유하는 단일 기준이다 (#526).
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
        // 이동을 멈추고 진행 중이던 배회 경로도 제거한다.
        //
        // ⚠ <b>에이전트가 살아 있을 때만</b> — 꺼져 있거나 NavMesh 밖이면 isStopped 접근이 예외를
        // 던진다(#557). 예전에는 이 상태로 오는 길이 전부 에이전트를 든 채였지만, 밧줄을 놓는 경로가
        // 그 전제를 깼다: 래그돌인 몸은 놓아도 에이전트를 되살리지 않고(NpcRopeDrag.StopRopeDrag의
        // 래그돌 가드) <b>일어날 때</b> 래그돌이 직접 붙인다 — 그 사이에 이 전이가 온다. (#572 후속)
        if (m_owner.AgentReady)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.ResetPath();
        }

        m_escapeTime = Time.time + m_config.EscapeSeconds;
        m_rejailTried = false;
    }

    public override void Tick()
    {
        if (StaysPut)
        {
            TryReturnToJail();
            return;
        }

        // 이미 일어나는 중 — 끝나면 도주로 이어진다. 그 사이 다시 묶이면 예약이 취소되고
        // 커스터디 재진입(Enter)이 타이머를 새로 잡는다. (#513)
        if (m_owner.StandUp.IsStandingUp)
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
        // 향후 이송·석방 등으로 풀릴 경우를 대비해 이동을 복구한다 —
        // 살아 있을 때만이다(Enter와 같은 이유). 꺼진 채 나가면 되살리는 쪽이 붙일 때 함께 푼다.
        if (m_owner.AgentReady)
            m_owner.Agent.isStopped = false;
    }

    /// <summary>방치돼도 그 자리에 남는 대상인가 — 판정 완료 = 인계 성공이라 방치 타이머에서 빠지고
    /// 본부에 얌전히 남는다 (#230). 유치장 안에 있는 대상도 남는다 (#526).
    ///
    /// 기준 자체는 <see cref="NpcStateRules.StaysPutWhenFreed"/>가 갖는다 — 밧줄 끊김(PlayerEscorter)이
    /// 같은 질문을 다르게 답하다 유치장 탈출을 만든 것이 #526이라, 답하는 곳을 하나로 모았다.
    /// 반출 방치 예외(#517)도 그쪽에 있다.
    ///
    /// 타이머 진입(<see cref="Tick"/>)과 <b>일어난 뒤 실행 직전</b>(<see cref="Flee"/>)이 같은 기준을 봐야 한다 —
    /// 둘 사이에 일어나기 대기(약 0.6초)가 끼면서 그 사이 판정이 통과할 수 있는 창이 생겼다 (#513).
    /// 방치된 대상이 마침 감옥 문 앞에 서 있으면 그 창에서 대상을
    /// 판정해 <see cref="NpcCustody.MarkDelivered"/>를 부르고, 재검사가 없으면 방금 인계된 신병이
    /// 그대로 달아난다.</summary>
    private bool StaysPut => NpcStateRules.StaysPutWhenFreed(m_owner);

    // 이번 Captured 동안 재수감을 이미 시도했는가 — 매 프레임 씬을 뒤지지 않게 한 번만 건다.
    private bool m_rejailTried;

    /// <summary>
    /// 감옥 안에서 멈춘 반출 수감자를 그 자리에서 다시 수감한다. (#537)
    ///
    /// 이게 없으면 <b>영원히 서 있는다</b>: 감옥 안은 <see cref="StaysPut"/>이라 방치 타이머가 돌지
    /// 않고(#526 — 잠긴 감옥에서 저절로 빠져나가지 않게 한 것), 그렇다고 수감 상태도 아니라
    /// 배회도 하지 않는다. 거리 이탈로 추종이 끊긴 반출 대상이 정확히 이 틈에 빠진다.
    ///
    /// 되돌릴 근거(판정 결과)가 없으면 <see cref="JailIntake.ServerReturnToJail"/>이 false를 주고
    /// 종전대로 그 자리에 선다 — 근거 없이 무료로 수감되지는 않는다.
    /// </summary>
    private void TryReturnToJail()
    {
        if (m_rejailTried || !m_owner.Custody.IsJailExtracted)
            return;

        m_rejailTried = true;

        // 감옥이 없는 씬에서는 null이라 아래 검사가 그대로 걸러 준다 (#592).
        JailIntake intake = App.Game.JailIntake;
        if (intake != null)
            intake.ServerReturnToJail(m_owner);
    }

    /// <summary>방치 타이머 만료 — 일어난 뒤 풀려나 달아난다. (#513)
    ///
    /// 묶인 대상은 누워 있으므로(#513) 만료 순간 곧바로 도주하면 누운 몸이 그대로 미끄러진다.
    /// 일어나기 모션을 먼저 태우고 그 길이만큼 지난 뒤 달아난다 — <b>그 구간이 곧 재포획 창</b>이다.
    /// 아직 묶인 채 <see cref="NpcState.Captured"/>이므로 달려가 E를 누르면 끌기가 재개되고
    /// (<see cref="NpcRopeDrag.StartRopeDrag"/>가 예약을 취소한다) 자세도 누운 상태로 되돌아간다.
    /// "일어난다 = 곧 달아난다"가 지금은 없던 예고 신호가 된다.
    ///
    /// 이미 서 있는 대상(제압만으로 잡혀 묶인 적 없는 Captured)은 기다리지 않고 곧바로 달아난다 —
    /// 그 판정은 <see cref="NpcStandUp.ServerStandUpThen"/>이 한다.</summary>
    private void Escape()
    {
        m_owner.StandUp.ServerStandUpThen(Flee);
    }

    /// <summary>일어난 뒤 실제로 달아난다.</summary>
    private void Flee()
    {
        // 일어나는 사이에 인계가 끝났으면 달아나지 않는다 — 이유는 StaysPut 주석 (#513).
        // 위치도 다시 본다: 일어나는 도중 누가 유치장 안으로 옮겨 놓았을 수 있다 (#526)
        if (StaysPut)
            return;

        // 밧줄은 소모형이 아니라 반환할 자원이 없다 — 상태 전이만으로 풀려난다. (#369)

        // 가장 가까운 플레이어를 위협 삼아 도주한다 — 반경은 저항 폴백(#205)·도주 회피(#213)와 같은
        // ThreatSearchRadius를 쓴다. 기준이 어긋나면 "도망칠 상대"와 "피할 상대"가 달라진다.
        PlayerHealth nearest = SuddenEventUtil.FindNearestFieldPlayer(
            m_owner.transform.position,
            m_owner.Reaction.ThreatSearchRadius
        );

        if (nearest != null)
        {
            Debug.Log($"인계 방치 — 풀려나 도주: {m_owner.name}");
            m_owner.Reaction.StartFlee(nearest.transform);
            return;
        }

        // 근처에 아무도 없으면 도망칠 이유도 없다 — 조용히 풀려나 배회로 복귀(사실상 탈출).
        // NpcResistState.Defeat의 폴백과 같은 패턴.
        Debug.Log($"인계 방치 — 풀려나 배회 복귀(주변에 플레이어 없음): {m_owner.name}");
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}
