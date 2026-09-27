/// <summary>
/// NPC 상태별 상호작용 가능 여부 규칙 모음 — 클라 조기검증·서버 가드·조준 피드백이 모두 여기를 읽는다.
/// </summary>
public static class NpcStateRules
{
    /// <summary>타격 피해가 들어가는 상태인지 판정한다(신병 확보·페널티군 제외).</summary>
    public static bool CanBeDamaged(NpcState state) =>
        state != NpcState.Dead
        && state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>이 NPC를 지금 때릴 수 있는지 판정한다(사망 제외, 납치범·소매치기 예외 허용).</summary>
    public static bool CanBeDamaged(NpcController npc) =>
        npc != null
        && npc.CurrentState != NpcState.Dead
        && (npc.Penalty.IsUndercoverDuty || CanBeDamaged(npc.CurrentState));

    /// <summary>환경 피해(차량·폭발)가 들어가는지 판정한다. 죽은 대상만 막는다.</summary>
    public static bool CanTakeEnvironmentalDamage(NpcController npc) =>
        npc != null && npc.CurrentState != NpcState.Dead;

    /// <summary>이 NPC를 지금 밧줄로 묶을 수 있는지 판정한다(소매치기 예외 포함).</summary>
    public static bool CanArrest(NpcController npc) =>
        npc != null
        && npc.CurrentState != NpcState.Dead
        && (npc.Penalty.IsPickpocketDuty || CanArrest(npc.CurrentState));

    /// <summary>스턴이 풀릴 때 도주로 전환되는 반응·배회군 상태인지 판정한다.</summary>
    public static bool IsReactive(NpcState state) =>
        state is NpcState.Idle
            or NpcState.Walk
            or NpcState.Run
            or NpcState.Attack
            or NpcState.Intruding;

    /// <summary>지금 새로 도주·저항 반응을 시작할 수 있는 상태인지 판정한다.</summary>
    public static bool CanStartReaction(NpcState state) =>
        IsReactive(state) && state != NpcState.Run && state != NpcState.Attack;

    /// <summary>맞았을 때 도주·저항으로 돌아설 수 있는 상태인지 판정한다.</summary>
    public static bool CanReactToDamage(NpcState state) =>
        CanStartReaction(state);

    /// <summary>신병 확보·타 시스템 소유 때문에 밧줄 대상에서 빠지는 상태가 아닌지 판정한다.</summary>
    public static bool CanArrest(NpcState state) =>
        state != NpcState.Dead
        && state != NpcState.Escorted
        && state != NpcState.Captured
        && state != NpcState.Jailed
        && state != NpcState.Detained
        && state != NpcState.Chasing
        && state != NpcState.PenaltyEscorting;

    /// <summary>밧줄로 새로 묶을 수 있는 대상(무력화 또는 시체)인지 판정한다.</summary>
    public static bool CanRopeBind(NpcController npc)
    {
        if (npc == null)
            return false;

        if (npc.Death.IsDead)
            return true;

        return npc.Stun.IsStunned && !IsPlayingStandUp(npc) && CanArrest(npc);
    }

    /// <summary>기상 모션이 실제로 재생 중인지 판정한다(재포획 창의 끝).</summary>
    public static bool IsPlayingStandUp(NpcController npc) =>
        npc != null && (npc.Stun.IsRising || npc.StandUp.IsPlayingStandUp);

    /// <summary>남이 끄는 대상에 밧줄을 덧걸어 합류할 수 있는 상태인지 판정한다.</summary>
    public static bool CanJoinDrag(NpcState state) => state == NpcState.Escorted;

    /// <summary>E로 풀어 석방할 수 있는 상태(Captured)인지 판정한다.</summary>
    public static bool CanRelease(NpcState state) => state == NpcState.Captured;

    /// <summary>줄이 풀려도 그 자리에 남아야 하는 신병인지 판정한다(감옥 방 안 또는 판정 완료). 서버(또는 오프라인) 전용.</summary>
    public static bool StaysPutWhenFreed(NpcController npc) =>
        npc != null
        && (JailRoom.Contains(npc.transform.position)
            || npc.Custody.IsDelivered);

    /// <summary>E 상호작용이 반응하는 상태(Captured·Jailed)인지 판정한다.</summary>
    public static bool HasInteractKeyAction(NpcState state) =>
        state is NpcState.Captured;

    /// <summary>방치 체력 회복이 적용될 수 있는 상태인지 판정한다(사망·기절·수감·호송 제외).</summary>
    public static bool CanRegenerate(NpcController npc) =>
        npc != null
        && !npc.Death.IsDead
        && !npc.Stun.IsStunned
        && npc.CurrentState != NpcState.Jailed
        && npc.CurrentState != NpcState.Escorted;
}
