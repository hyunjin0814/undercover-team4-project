using UnityEngine;

/// <summary>
/// 순간 이벤트로 오는 단발 모션(스윙·기상)을 기준 모션 위에 잠시 덮어쓰도록 제안하는 부품.
/// </summary>
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcOneShotMotion : MonoBehaviour, INpcMotionSource
{
    [Header("공격 스윙 (#220)")]
    [Tooltip("스윙 1회당 Attack(단발) 모션을 유지하는 시간(초) — 이후 버틴 자세로 복귀한다. 저항 공격 주기보다 짧고 타격 오프셋보다 길게")]
    [SerializeField] private float m_swingAnimSeconds = 0.9f;

    [Header("일어나기 (#269/#513)")]
    [Tooltip("일어나기 모션 유지 시간(초). NpcStunConfig.StandUpSeconds와 같게 둘 것")]
    [SerializeField] private float m_standUpSeconds = 1.17f;

    private NpcAnimationDriver m_driver;

    private float m_swingUntil;

    private bool m_standingUp;
    private float m_standUpUntil;

    public ENpcMotionPriority Priority => ENpcMotionPriority.OneShot;

    public bool IsStandingUp => m_standingUp;

    private void Awake() => m_driver = GetComponent<NpcAnimationDriver>();

    /// <summary>서버가 뽑은 variant 클립으로 공격 스윙을 1회 재생한다.</summary>
    public void PlaySwing(int variant)
    {
        if (!CanSwingIn(m_driver.BaseState))
            return;

        m_driver.SetSwingVariant(variant);
        m_swingUntil = Time.time + m_swingAnimSeconds;
    }

    /// <summary>누운 자세에서 일어나는 모션을 재생한다.</summary>
    public void PlayStandUp()
    {
        if (m_driver.BaseState != NpcState.Stunned && m_driver.BaseState != NpcState.Captured)
            return;

        m_standingUp = true;
        m_standUpUntil = Time.time + m_standUpSeconds;
        m_driver.RefreshProne();
    }

    /// <summary>다시 묶였을 때 일어나기 모션을 취소하고 누운 자세로 되돌린다.</summary>
    public void CancelStandUp()
    {
        m_standingUp = false;
        m_standUpUntil = 0f;
    }

    /// <summary>기준 상태가 바뀌면 진행 중인 단발 모션을 정리한다.</summary>
    public void OnBaseStateChanged(NpcState state)
    {
        m_swingUntil = 0f;

        if (state is NpcState.Stunned or NpcState.Escorted or NpcState.Captured or NpcState.Dead)
            CancelStandUp();
    }

    /// <summary>유지 시간을 진행한다 — 드라이버가 결정 직전에 부른다.</summary>
    public void Tick()
    {
        if (m_swingUntil > 0f && Time.time >= m_swingUntil)
            m_swingUntil = 0f;

        if (m_standUpUntil > 0f && Time.time >= m_standUpUntil && m_driver.CanLeaveStandUp)
        {
            m_standUpUntil = 0f;
        }
    }

    public bool TryGetMotion(out int animState)
    {
        if (m_swingUntil > 0f)
        {
            animState = (int)NpcState.Attack;
            return true;
        }

        if (m_standUpUntil > 0f)
        {
            animState = NpcAnimStates.k_standUp;
            return true;
        }

        animState = 0;
        return false;
    }

    /// <summary>스윙 모션이 가능한 기준 상태인지 판정한다(구속·무력화·페널티 제외).</summary>
    private static bool CanSwingIn(NpcState state) =>
        state is not (
            NpcState.Captured or NpcState.Escorted or NpcState.Stunned
            or NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting
        );
}
