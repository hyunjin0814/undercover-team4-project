public enum ENpcMotionPriority
{
    Locomotion = 1,

    OneShot = 2,
}

/// <summary>
/// 원하는 Animator 상태 번호와 우선순위를 NpcAnimationDriver에 보고하는 모션 부품 인터페이스.
/// </summary>
public interface INpcMotionSource
{
    ENpcMotionPriority Priority { get; }

    /// <summary>원하는 Animator 상태 번호를 돌려준다. 없으면 false.</summary>
    bool TryGetMotion(out int animState);
}
