using UnityEngine;

/// <summary>
/// transform 실제 이동량으로 이동/정지 모션을 제안하는 부품(연행·수감·저항·침입·페널티 상태용).
/// 경계에 히스테리시스를 둬 떨림을 막는다.
/// </summary>
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcLocomotionMotion : MonoBehaviour, INpcMotionSource
{
    private const float k_moveOnSpeed = 0.5f;
    private const float k_moveOffSpeed = 0.25f;

    private const float k_speedSmoothing = 25f;

    private const float k_penaltyRunOnSpeed = 4f;
    private const float k_penaltyRunOffSpeed = 3.4f;

    [Header("자물쇠 해제 (#261)")]
    [Tooltip("해제 시작(Begin) 모션을 유지하는 시간(초) — 이후 반복(Loop)으로 넘어간다. Begin 클립 길이(0.63초)에 맞춘 값")]
    [SerializeField] private float m_unlockBeginSeconds = 0.63f;

    private NpcAnimationDriver m_driver;

    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;

    private bool m_escortMoving;
    private bool m_resistMoving;
    private bool m_intrudeMoving;
    private int m_speedTierMotion;

    private float m_unlockBeginUntil;

    public ENpcMotionPriority Priority => ENpcMotionPriority.Locomotion;

    public bool IsResistMoving => m_resistMoving;

    private void Awake() => m_driver = GetComponent<NpcAnimationDriver>();

    /// <summary>기준 상태가 바뀌면 판별 플래그와 속도 평활을 새 상태에 맞춰 초기화한다.</summary>
    public void OnBaseStateChanged(NpcState state)
    {
        m_unlockBeginUntil = 0f;

        if (state == NpcState.Attack)
        {
            m_resistMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsHandcuffedMotion(state))
        {
            m_escortMoving = true;
            Reseed(k_moveOnSpeed);
        }
        else if (IsSpeedTierMotion(state))
        {
            m_speedTierMotion = state == NpcState.Chasing ? (int)NpcState.Run : (int)NpcState.Walk;
            Reseed(state == NpcState.Chasing ? k_penaltyRunOnSpeed : k_moveOnSpeed);
        }
        else if (state == NpcState.Intruding)
        {
            m_intrudeMoving = true;
            Reseed(k_moveOnSpeed);
        }
    }

    /// <summary>속도 추적을 지금 위치에서 다시 시작한다 — 풀린 직후 속도가 튀지 않게.</summary>
    public void ResetSpeedTracking() => Reseed(0f);

    private void Reseed(float speed)
    {
        m_lastPosition = transform.position;
        m_smoothedSpeed = speed;
    }

    /// <summary>속도를 갱신하고 판별을 진행한다 — 멈춰야 하는 구간에서는 위치만 따라간다.</summary>
    public void Tick(float deltaTime)
    {
        if (m_driver.SuppressLocomotion)
        {
            m_lastPosition = transform.position;
            return;
        }

        if (!IsSpeedDriven(m_driver.BaseState))
            return;

        float rawSpeed = (transform.position - m_lastPosition).magnitude / deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, deltaTime * k_speedSmoothing);

        switch (m_driver.BaseState)
        {
            case NpcState.Attack:
                TickResist();
                break;
            case NpcState.Intruding:
                TickIntrude();
                break;
            default:
                if (IsSpeedTierMotion(m_driver.BaseState))
                    TickSpeedTier();
                else
                    TickEscort();
                break;
        }
    }

    public bool TryGetMotion(out int animState)
    {
        animState = 0;
        if (m_driver.SuppressLocomotion || !IsSpeedDriven(m_driver.BaseState))
            return false;

        switch (m_driver.BaseState)
        {
            case NpcState.Attack:
                animState = m_resistMoving ? (int)NpcState.Run : (int)NpcState.Idle;
                return true;

            case NpcState.Intruding:
                animState = m_intrudeMoving
                    ? (int)NpcState.Walk
                    : (m_unlockBeginUntil > 0f ? NpcAnimStates.k_unlockingBegin : NpcAnimStates.k_unlockingLoop);
                return true;

            default:
                if (IsSpeedTierMotion(m_driver.BaseState))
                {
                    animState = m_speedTierMotion;
                    return true;
                }

                animState = m_escortMoving ? (int)NpcState.Escorted : (int)NpcState.Captured;
                return true;
        }
    }

    private void TickResist()
    {
        if (m_resistMoving && m_smoothedSpeed < k_moveOffSpeed)
            m_resistMoving = false;
        else if (!m_resistMoving && m_smoothedSpeed > k_moveOnSpeed)
            m_resistMoving = true;
    }

    private void TickIntrude()
    {
        if (m_intrudeMoving && m_smoothedSpeed < k_moveOffSpeed)
        {
            m_intrudeMoving = false;
            m_unlockBeginUntil = Time.time + m_unlockBeginSeconds;
        }
        else if (!m_intrudeMoving && m_smoothedSpeed > k_moveOnSpeed)
        {
            m_intrudeMoving = true;
            m_unlockBeginUntil = 0f;
        }
        else if (m_unlockBeginUntil > 0f && Time.time >= m_unlockBeginUntil)
        {
            m_unlockBeginUntil = 0f;
        }
    }

    private void TickSpeedTier()
    {
        if (m_smoothedSpeed < k_moveOffSpeed)
            m_speedTierMotion = (int)NpcState.Idle;
        else if (m_smoothedSpeed > k_penaltyRunOnSpeed)
            m_speedTierMotion = (int)NpcState.Run;
        else if (m_smoothedSpeed > k_moveOnSpeed && m_smoothedSpeed < k_penaltyRunOffSpeed)
            m_speedTierMotion = (int)NpcState.Walk;
    }

    private void TickEscort()
    {
        if (m_escortMoving && m_smoothedSpeed < k_moveOffSpeed)
            m_escortMoving = false;
        else if (!m_escortMoving && m_smoothedSpeed > k_moveOnSpeed)
            m_escortMoving = true;
    }

    /// <summary>이 기준 상태의 모션을 속도로 가르는가 — 아니면 기준 상태 모션이 그대로 쓰인다.</summary>
    private static bool IsSpeedDriven(NpcState state) =>
        IsHandcuffedMotion(state)
        || IsSpeedTierMotion(state)
        || state == NpcState.Attack
        || state == NpcState.Intruding;

    /// <summary>모션이 속도 3단(Idle/Walk/Run)으로 갈리는 상태(페널티·밀수 운반)인지 판정한다.</summary>
    private static bool IsSpeedTierMotion(NpcState state) =>
        IsPenaltyLocomotion(state) || state == NpcState.Smuggling;

    /// <summary>수갑 찬 채 이동하는 상태인가 — 수감은 대응 Animator 상태가 없어 연행 모션을 빌린다.</summary>
    public static bool IsHandcuffedMotion(NpcState state) =>
        state is NpcState.Escorted or NpcState.Jailed;

    /// <summary>오검거 페널티 상태인가 — 앵그리 마크 표시 조건과 같은 집합이다. (#277~#279)</summary>
    public static bool IsPenaltyLocomotion(NpcState state) =>
        state is NpcState.Detained or NpcState.Chasing or NpcState.PenaltyEscorting;
}
