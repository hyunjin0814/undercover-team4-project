/// <summary>
/// NPC 상태 → 상호작용 가능성 해석 규칙. (#184)
/// 클라 조기검증(Rope)·서버 가드(PlayerEscorter)·조준 피드백(InteractionFeedback)이
/// 모두 여기를 읽는다 — 새 상태 추가 시 이 파일만 고치면 셋이 함께 움직인다.
/// 상태별 '행동'은 NpcXxxState 클래스(FSM, 서버 전용), 상태별 '가능 여부'는 여기 — 역할 분리.
/// 클라이언트는 동기화된 enum(NpcController.CurrentState)만 알기 때문에 순수 함수로 둔다.
/// </summary>
public static class NpcStateRules
{
    /// <summary>수갑 체포 채널링의 대상이 될 수 있는 상태인가.
    /// 제외 목록 방식 — 새 상태는 기본 '체포 가능'이므로 막아야 하면 여기 추가할 것.
    /// 도주(Run)·저항(Attack)은 수갑이 아니라 E 제압 홀드·테이저로만 잡는다 (GDD 6-1/7-4, #254) —
    /// 반응이 시작된 뒤에는 수갑 채널링이 걸리지 않아야 한다.</summary>
    public static bool IsCapturable(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Run
        && state != NpcState.Attack
        // 오검거 페널티에 얽힌 시민(수용·추격·호송)은 다시 수갑을 채울 수 없다 (#277~#279) —
        // 추격대를 체포해 페널티 집행을 무산시키는 우회를 막는다 (회피 수단은 격퇴(호루라기 #250)뿐)
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>타격 피해가 들어가는 상태인가 — <b>스턴 게이트가 아니다.</b> (#292)
    /// 스턴은 오버레이가 되면서 전 상태에 걸리게 됐지만(#292), 타격까지 함께 열면 연행 중인
    /// NPC를 때려 기절시켜 신병에서 빼내는 우회가 생긴다. 그래서 게이트를 둘로 쪼개고
    /// 이쪽은 구 CanBeStunned(#289)의 제외 목록을 그대로 물려받았다 — 팀 결정은
    /// "스턴은 허용, 타격은 차단"이다.
    ///
    /// 제외하는 건 이미 신병을 확보(Escorted/Captured/Jailed)했거나 오검거 페널티가 진행(Detained/
    /// Chasing/PenaltyEscorting) 중인 상태 — 도주(Run)·저항(Attack)은 주 타격 대상이라 제외하지 않는다.</summary>
    public static bool CanBeDamaged(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>반응·배회군인가 — 스턴이 풀릴 때 도주로 전환되는 쪽. (#292)
    /// 여집합(확보·페널티군 + Holding)은 스턴이 풀려도 아무 전이 없이 하던 일을 재개한다 —
    /// 상태 enum이 애초에 안 바뀌므로 호송·수감·페널티·정리 링크가 그대로 살아 있다.
    ///
    /// 포함 목록 방식이라 <b>새 상태는 기본이 '재개'</b>다. 도주로 깨어나야 하면 여기 추가할 것.
    /// 의도적으로 뺀 둘: <see cref="NpcState.Holding"/>(#291 — 임시 거처로 걸어가 소멸하는 정리
    /// 대상이라 도주시키면 경로가 끊긴다)과 <see cref="NpcState.Stunned"/>(넉백 KO — 자기 상태
    /// 클래스가 스스로 빠져나간다).</summary>
    public static bool IsReactive(NpcState state) =>
        state is NpcState.Idle
            or NpcState.Walk
            or NpcState.Run
            or NpcState.Attack
            or NpcState.Intruding;

    /// <summary>지금 새로 반응(도주·저항)을 시작할 수 있는 상태인가. (#400)
    /// <see cref="IsReactive"/>에서 이미 반응 중인 둘(Run·Attack)을 뺀 집합 — 스캔·타격이 연달아
    /// 들어와도 진행 중인 반응을 갈아엎지 않는다. 확보·페널티군은 IsReactive가 이미 걸러 준다.</summary>
    public static bool CanStartReaction(NpcState state) =>
        IsReactive(state) && state != NpcState.Run && state != NpcState.Attack;

    /// <summary>밧줄로 묶어 끌 수 있는 상태인가. (#269 → #369 기본 검거로 승격)
    /// 제외 목록 방식 — 이미 신병 확보(Escorted/Captured/Jailed)·타 시스템 소유(Holding·페널티)는 제외.
    /// 기절·도주·저항 등 나머지는 전부 대상이다(제압 타격으로 HP 0에 쓰러진 저항형 Stunned 포함, #366).
    /// Captured 제외 주의: 그 상태에선 밧줄 좌클릭이 '풀어주기'로 갈리고(<see cref="CanRelease"/>),
    /// 다시 끄는 건 E 경로다.</summary>
    public static bool CanArrest(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Holding
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>이미 남이 끌고 있는 대상에 밧줄을 <b>덧걸</b> 수 있는가 — 줄다리기 합류. (#390)
    /// 팀 결정은 "합류는 허용, 탈취는 차단"이다. 합류는 기존 끌기를 끊지 않고 참가자만 하나 늘린다.
    /// 그래서 <see cref="CanArrest"/>의 <see cref="NpcState.Escorted"/> 제외를 <b>건드리지 않고</b>
    /// 규칙을 따로 판다 — 그쪽을 열면 "새로 묶기" 경로가 통째로 열려 탈취가 딸려온다.
    /// 같은 이유로 놓아둔 체포(Captured)는 뺀다: 남의 소유로 서 있는 대상이라 그게 곧 탈취다.</summary>
    public static bool CanJoinDrag(NpcState state) => state == NpcState.Escorted;

    /// <summary>밧줄 좌클릭으로 풀어 석방할 수 있는 상태인가 — 체포되어 멈춘 대상(Captured)만. (#290 → #369)
    /// 밧줄은 소모형이 아니라 상태만으로 가른다(수갑 시절의 자원 유무 조건 없음). 제압만으로 잡힌 Captured도 대상.</summary>
    public static bool CanRelease(NpcState state) => state == NpcState.Captured;

    /// <summary>E 상호작용(제압·타격·재연행)이 반응하는 상태인가.
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    /// 배회(Idle/Walk)가 열린 것은 체력이 지속형이 되면서다 (#366) — 예전에는 '배회 NPC 폭행 방지'로
    /// 막혀 있었지만, 이제 아무 때나 때려 체력을 깎을 수 있다.</summary>
    public static bool HasSubdueInteraction(NpcState state) =>
        state is NpcState.Idle or NpcState.Walk or NpcState.Run or NpcState.Attack or NpcState.Captured;
}
