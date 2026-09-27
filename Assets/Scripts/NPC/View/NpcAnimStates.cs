using UnityEngine;

/// <summary>
/// NPC Animator State 파라미터 번호와 파라미터 이름 모음 — 런타임과 컨트롤러 빌더가 공유한다.
/// 0~99는 NpcState 값, 100번대는 FSM 상태가 아닌 모션 전용 번호다.
/// </summary>
public static class NpcAnimStates
{
    public const string k_stateParam = "State";

    public const string k_swingVariantParam = "SwingVariant";

    public static readonly int s_stateHash = Animator.StringToHash(k_stateParam);
    public static readonly int s_swingVariantHash = Animator.StringToHash(k_swingVariantParam);

    public const int k_unlockingBegin = 100;

    public const int k_unlockingLoop = 101;

    public const int k_standUp = 102;

    public const int k_sitBegin = 107;

    public const int k_sitLoop = 108;
}
