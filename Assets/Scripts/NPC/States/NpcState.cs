/// <summary>
/// NPC 상태 enum — GDD 10-2 기준. 이번 이슈에서는 Idle/Walk만 구현한다.
/// </summary>
public enum NpcState
{
    Idle,
    Walk,
    Run,
    Attack,
    Captured,
    Stunned,
    Escorted,

    Jailed,

    Intruding,

    Detained,

    Chasing,

    PenaltyEscorting,

    Dead,

    Sprinting,

    Smuggling,
}
