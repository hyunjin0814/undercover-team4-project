/// <summary>
/// NPC 상태 → 상호작용 가능성 해석 규칙. (#184)
/// 클라 조기검증(Rope)·서버 가드(PlayerEscorter)·조준 피드백(InteractionFeedback)이
/// 모두 여기를 읽는다 — 새 상태 추가 시 이 파일만 고치면 셋이 함께 움직인다.
/// 상태별 '행동'은 NpcXxxState 클래스(FSM, 서버 전용), 상태별 '가능 여부'는 여기 — 역할 분리.
/// 대부분은 동기화된 enum(NpcController.CurrentState)만 보는 순수 함수다 — 클라도 그것만 알기 때문.
/// 예외는 <see cref="CanRopeBind"/> 하나 — 무력화 여부가 상태 enum에 없어 NpcController를 받는다 (#446).
/// </summary>
public static class NpcStateRules
{
    /// <summary>수갑 체포 채널링의 대상이 될 수 있는 상태인가.
    /// 제외 목록 방식 — 새 상태는 기본 '체포 가능'이므로 막아야 하면 여기 추가할 것.
    /// 도주(Run)·저항(Attack)은 수갑이 아니라 진압봉·테이저로 기절시킨 뒤 밧줄로 잡는다
    /// (GDD 6-1/7-4, #254 · E 제압은 #436·#438에서 전부 제거) —
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

    /// <summary>밧줄 대상에서 <b>신병·소유권 때문에</b> 빠지는 상태인가. (#269 → #369 기본 검거로 승격)
    /// 제외 목록 방식 — 이미 신병 확보(Escorted/Captured/Jailed)·타 시스템 소유(Holding·페널티)는 제외.
    /// Captured 제외 주의: 그 상태에선 밧줄 좌클릭이 '풀어주기'로 갈리고(<see cref="CanRelease"/>),
    /// 다시 끄는 건 E 경로다.
    ///
    /// <b>이것만으로 묶기를 판정하지 말 것</b> — 새로 묶기는 무력화까지 요구하므로
    /// <see cref="CanRopeBind"/>가 정본이고 이 함수는 그 한 조각이다 (#446).</summary>
    public static bool CanArrest(NpcState state) =>
        state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Holding
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>밧줄 좌클릭으로 <b>새로 묶을</b> 수 있는 대상인가 — 무력화된 대상만. (#446)
    /// 깨어 있는 NPC를 좌클릭 3초 홀드로 묶던 경로가 제거되면서 묶기의 전제가 무력화가 됐다.
    /// 역할이 완전히 갈린다: 체력 깎기는 진압봉, 즉시 무력화는 테이저, 신병 확보는 밧줄.
    /// 홀드가 없어졌으므로 이 판정을 통과한 대상은 좌클릭 한 번에 즉시 묶인다 —
    /// 원래 기절 대상에만 있던 지름길이 유일한 경로가 된 것이다 (PlayerEscorter.ServerBeginRopeDrag).
    ///
    /// 상태 enum이 아니라 <see cref="NpcController.IsStunned"/>를 보는 이유: 스턴은 오버레이라
    /// 테이저·체력 0 기절이 CurrentState를 바꾸지 않는다(넉백 KO만 <see cref="NpcState.Stunned"/>).
    /// 상태로만 보면 두 기절 경로 중 하나가 조용히 빠진다 (#292). IsStunned는 동기화 값이라
    /// 클라 조기검증·조준 피드백(Rope)에서도 읽을 수 있다.</summary>
    public static bool CanRopeBind(NpcController npc) =>
        npc != null && npc.IsStunned && CanArrest(npc.CurrentState);

    /// <summary>밧줄 없이 따라오는 수감자인가 — 유치장에서 반출돼 추종 중인 대상. (#492)
    /// E를 누르면 그 자리에 세운다(Captured) — 유치장 안이면 JailIntake가 좌석에 다시 앉히고,
    /// 밖이면 그냥 선다(팀 확정 2026-08-03 "위치로 갈린다").
    ///
    /// 상태 enum만으로는 못 가른다 — 밧줄 끌기도 같은 <see cref="NpcState.Escorted"/>다.
    /// 그래서 <see cref="NpcController.IsRoped"/>를 함께 본다(<see cref="CanRopeBind"/>와 같은 이유로
    /// NpcController를 받는다). IsRoped는 동기화 값이라 클라 조준 피드백에서도 읽을 수 있다.</summary>
    public static bool IsFollowingUnroped(NpcController npc) =>
        npc != null && npc.CurrentState == NpcState.Escorted && !npc.IsRoped;

    /// <summary>멈춰 선 반출 수감자인가 — E로 <b>밧줄 없는 추종</b>을 재개할 수 있는 대상. (#517)
    /// 반출된 대상은 거리가 벌어지면 <see cref="NpcEscortedState"/>가 Captured로 되돌려 세우는데,
    /// 상태만 보면 방금 제압한 신병과 구분되지 않아 E가 밧줄 끌기로 샜다 — 반출 흐름으로 되돌릴 입력이
    /// 없어지는 것이 #517의 증상이다. 그래서 상태 대신 <see cref="NpcController.IsJailExtracted"/>를
    /// 함께 본다(<see cref="CanRopeBind"/>·<see cref="IsFollowingUnroped"/>와 같은 이유로 NpcController를 받는다).
    ///
    /// 밧줄이 걸린 대상은 여기 오지 않는다 — 묶이는 순간 표식이 꺼져(NpcController.StartRopeDrag)
    /// E가 다시 밧줄 재개로 간다. 두 분기가 겹치지 않는 근거가 그것이다.</summary>
    public static bool CanResumeUnropedEscort(NpcController npc) =>
        npc != null && npc.CurrentState == NpcState.Captured && npc.IsJailExtracted;

    /// <summary>이미 남이 끌고 있는 대상에 밧줄을 <b>덧걸</b> 수 있는가 — 줄다리기 합류. (#390)
    /// 팀 결정은 "합류는 허용, 탈취는 차단"이다. 합류는 기존 끌기를 끊지 않고 참가자만 하나 늘린다.
    /// 그래서 <see cref="CanArrest"/>의 <see cref="NpcState.Escorted"/> 제외를 <b>건드리지 않고</b>
    /// 규칙을 따로 판다 — 그쪽을 열면 "새로 묶기" 경로가 통째로 열려 탈취가 딸려온다.
    /// 같은 이유로 놓아둔 체포(Captured)는 뺀다: 남의 소유로 서 있는 대상이라 그게 곧 탈취다.</summary>
    public static bool CanJoinDrag(NpcState state) => state == NpcState.Escorted;

    /// <summary>밧줄 좌클릭으로 풀어 석방할 수 있는 상태인가 — 체포되어 멈춘 대상(Captured)만. (#290 → #369)
    /// 밧줄은 소모형이 아니라 상태만으로 가른다(수갑 시절의 자원 유무 조건 없음). 제압만으로 잡힌 Captured도 대상.</summary>
    public static bool CanRelease(NpcState state) => state == NpcState.Captured;

    /// <summary>E 상호작용이 반응하는 상태인가 — 이제 <b>신병 조작 전용</b>이다. (#438/#492)
    /// 포함 목록 방식 — 새 상태는 기본 'E 불가'이므로 열어야 하면 여기 추가할 것.
    /// NpcSubdueInteractable.Interact의 분기 집합과 반드시 일치해야 한다.
    ///
    /// 두 단계로 좁혀졌다: 도주형 3초 제압 홀드 제거(#436)로 <c>Run</c>이 타격 분기에 합쳐졌고,
    /// 제압 타격 자체가 제거(#438)되면서 배회(Idle/Walk)·도주(Run)·저항(Attack)이 전부 빠졌다.
    /// 때리는 것은 진압봉, 즉시 무력화는 테이저, 신병 확보는 밧줄이 맡는다.
    ///
    /// 남은 둘: <c>Captured</c>는 재연행(밧줄 끌기 재개), <c>Jailed</c>는 <b>유치장 반출</b>이다 —
    /// 앉은 수감자를 일으켜 밧줄 없이 따라오게 한다 (#492). 이미 확보가 끝난 대상이라
    /// 무력화도 채널링도 요구하지 않는다.
    /// 끌리는 중(<c>Escorted</c>)의 줄다리기 복귀는 상태가 아니라 "누구의 줄인가"로 갈리므로
    /// 순수 함수인 여기가 아니라 호출부가 판단한다 (#398).
    ///
    /// 개명 이력: <c>HasSubdueInteraction</c> → 제압(subdue) 동작이 E에서 전부 빠져 이름이
    /// 실제 역할과 어긋나게 되어 #438에서 바꿨다.</summary>
    public static bool HasInteractKeyAction(NpcState state) =>
        state is NpcState.Captured or NpcState.Jailed;
}
