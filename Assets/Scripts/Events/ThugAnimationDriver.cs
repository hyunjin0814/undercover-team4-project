using UnityEngine;

/// <summary>
/// 괴한(<see cref="ThugAttacker"/>) 전용 애니메이션 구동 — Animator의 State(int) 파라미터를 갱신한다. (#106)
/// 괴한은 <see cref="NpcController"/>가 아니라 FSM/상태 동기화가 없으므로, 표현에 필요한 최소한만 로컬에서 유도한다:
///  · <b>이동</b> — transform 이동량으로 속도를 추정해 Run/Idle을 전환하고, 달리기 클립의 재생속도를
///    실제 이동 속도에 비례시켜 발 미끄러짐을 줄인다. 서버는 NavMeshAgent가,
///    클라이언트는 NetworkTransform이 transform을 움직이므로 별도 동기화 없이 전 피어에서 동작한다.
///  · <b>타격</b> — <see cref="ThugAttacker.OnAttack"/>(서버가 ClientRpc로 전 피어 복제)을 구독해
///    잠시 Attack 모션을 재생한 뒤 이동 상태로 복귀한다.
/// Animator 상태 번호는 <see cref="NpcState"/> 값 규약을 그대로 따른다(NPC.controller 공용). (NpcAnimationDriver와 동일)
/// </summary>
[RequireComponent(typeof(ThugAttacker))]
public class ThugAnimationDriver : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");

    // Base Layer 달리기 클립의 재생속도 배율 — 실제 이동 속도에 맞춰 발 미끄러짐을 줄인다.
    // (NpcAnimationDriver의 패닉 다리 처리와 동일 기법. 기본값 1이라 이 값을 안 쓰는 NPC는 영향 없음)
    private static readonly int s_runSpeedHash = Animator.StringToHash("RunSpeedMul");

    // 프레임 노이즈 완화용 지수 평활 계수 — 클수록 정지/이동 반응이 빨라진다. (NpcAnimationDriver와 동일 개념)
    private const float k_speedSmoothing = 12f;

    // 재생속도 배율 허용 범위 — 과하게 늘리거나 줄이면 슬로모션/과속처럼 보인다. (NpcAnimationDriver와 동일)
    private const float k_runSpeedMulMin = 0.2f;
    private const float k_runSpeedMulMax = 2f;

    [Header("이동 판별")]
    [Tooltip("이 추정 속도(m/s) 이상이면 달리기(Run), 미만이면 정지(Idle)로 본다")]
    [SerializeField]
    private float m_moveSpeedThreshold = 0.3f;

    // 클립이 in-place(루트모션 없음)라 자동 계산이 불가능한 값 — 눈으로 보며 미세 튜닝할 것.
    // NpcAnimationDriver.m_panicRunReferenceSpeed와 같은 클립(HumanM@Run01_Forward)이라 기본값도 같다.
    [Tooltip("달리기 클립이 미끄럼 없이 보이는 기준 지상 속도(m/s). 발이 앞으로 밀리면 값을 낮추고, 뒤로 끌리면 높인다")]
    [SerializeField]
    private float m_runReferenceSpeed = 4.5f;

    [Header("타격 모션")]
    [Tooltip("타격 스윙 1회당 Attack 모션을 유지하는 시간(초) — 이후 이동 상태로 복귀한다")]
    [SerializeField]
    private float m_attackAnimDuration = 0.6f;

    [SerializeField]
    private Animator m_animator;

    private ThugAttacker m_thug;
    private Vector3 m_lastPosition;
    private float m_smoothedSpeed;
    private float m_attackAnimUntil;
    private int m_appliedState = -1; // 마지막으로 Animator에 쓴 값 — 매 프레임 중복 SetInteger 방지

    private void Awake()
    {
        m_thug = GetComponent<ThugAttacker>();
        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void Start()
    {
        // 타격 알림은 전 피어에서 발생한다 — 서버는 로컬 발행, 원격 클라는 ClientRpc 중계 (#56)
        m_thug.OnAttack += HandleAttack;
        m_lastPosition = transform.position;

        // 괴한은 스폰 직후 바로 추격에 들어간다 — 평활 속도를 기준 속도로 시드해 두지 않으면
        // 0에서 올라오는 동안 첫 걸음이 슬로모션으로 보인다. (NpcAnimationDriver의 패닉 진입과 동일)
        m_smoothedSpeed = m_runReferenceSpeed;
        if (m_animator != null)
            m_animator.SetFloat(s_runSpeedHash, 1f);
    }

    private void OnDestroy()
    {
        if (m_thug != null)
            m_thug.OnAttack -= HandleAttack;
    }

    private void HandleAttack()
    {
        m_attackAnimUntil = Time.time + m_attackAnimDuration;
    }

    private void Update()
    {
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        // transform 이동량 기반 속도 추정 — 서버(NavMeshAgent)·클라(NetworkTransform) 모두에서 유효
        float rawSpeed = (transform.position - m_lastPosition).magnitude / Time.deltaTime;
        m_lastPosition = transform.position;
        m_smoothedSpeed = Mathf.Lerp(m_smoothedSpeed, rawSpeed, Time.deltaTime * k_speedSmoothing);

        int desired;
        if (Time.time < m_attackAnimUntil)
            desired = (int)NpcState.Attack;
        else
            desired =
                m_smoothedSpeed >= m_moveSpeedThreshold ? (int)NpcState.Run : (int)NpcState.Idle;

        if (desired != m_appliedState)
        {
            m_appliedState = desired;
            m_animator.SetInteger(s_stateHash, desired);
        }

        // 달리는 동안에만 배율을 갱신한다 — 멈춰 있을 때의 0에 가까운 속도로 배율을 눌러두면
        // 다시 달리기 시작하는 첫 프레임이 슬로모션으로 보인다.
        if (desired == (int)NpcState.Run)
        {
            float mul = Mathf.Clamp(
                m_smoothedSpeed / m_runReferenceSpeed,
                k_runSpeedMulMin,
                k_runSpeedMulMax
            );
            m_animator.SetFloat(s_runSpeedHash, mul);
        }
    }
}
