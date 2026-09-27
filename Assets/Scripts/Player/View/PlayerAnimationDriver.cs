using UnityEngine;

/// <summary>
/// 위치 변화로 잰 이동 속도를 걷기/달리기 기준으로 정규화해 Animator MoveX/MoveZ에 전달한다.
/// 원격 플레이어에서도 그대로 동작한다.
/// </summary>
public class PlayerAnimationDriver : MonoBehaviour
{
    public const float k_walkParam = 1f;
    public const float k_runParam = 2f;

    public const float k_swingImpactSeconds = 0.3f;

    public const float k_swingSeconds = 0.64f;

    private static readonly int s_moveXHash = Animator.StringToHash("MoveX");
    private static readonly int s_moveZHash = Animator.StringToHash("MoveZ");
    private static readonly int s_downHash = Animator.StringToHash("Down");
    private static readonly int s_crouchHash = Animator.StringToHash("Crouch");
    private static readonly int s_airborneHash = Animator.StringToHash("Airborne");
    private static readonly int s_attackHash = Animator.StringToHash("Attack");
    private static readonly int s_revivingHash = Animator.StringToHash("Reviving");

    [SerializeField]
    private Animator m_animator;

    [SerializeField]
    private PlayerMovement m_movement;

    [SerializeField]
    private float m_damping = 0.1f;

    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private PlayerHandView m_handView;
    private PlayerRagdoll m_ragdoll;
    private PlayerReviver m_reviver;
    private Vector3 m_lastPosition;

    private void Awake()
    {
        if (m_animator == null)
        {
            m_animator = GetComponentInChildren<Animator>();
        }

        if (m_movement == null)
        {
            m_movement = GetComponentInParent<PlayerMovement>();
        }

        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_crouch = GetComponentInParent<PlayerCrouch>();
        m_jump = GetComponentInParent<PlayerJump>();
        m_handView = GetComponentInParent<PlayerHandView>();
        m_ragdoll = GetComponentInParent<PlayerRagdoll>();
        m_reviver = GetComponentInParent<PlayerReviver>();
        m_lastPosition = transform.position;
    }

    /// <summary>3인칭 상체 타격 트리거와 1인칭 팔 스윙을 함께 재생한다. 모든 피어에서 호출된다.</summary>
    public void TriggerAttack()
    {
        if (m_animator != null)
        {
            m_animator.SetTrigger(s_attackHash);
        }

        if (m_handView != null)
        {
            m_handView.PlaySwing();
        }
    }

    private void Update()
    {
        if (m_animator == null)
            return;

        if (m_incapacitation != null)
        {
            bool prone =
                m_incapacitation.IsProne || (m_ragdoll != null && m_ragdoll.IsRagdollActive);
            m_animator.SetBool(s_downHash, prone);
        }

        if (m_crouch != null)
        {
            m_animator.SetBool(s_crouchHash, m_crouch.IsCrouchRequested);
        }

        if (m_jump != null)
        {
            m_animator.SetBool(s_airborneHash, m_jump.IsAirborne);
        }

        if (m_reviver != null)
        {
            m_animator.SetBool(s_revivingHash, m_reviver.IsChanneling);
        }

        if (m_movement == null || Time.deltaTime <= 0f)
            return;

        Vector3 worldDelta = transform.position - m_lastPosition;
        worldDelta.y = 0f;
        m_lastPosition = transform.position;

        Vector3 localVelocity = transform.InverseTransformDirection(worldDelta / Time.deltaTime);
        Vector2 param = NormalizeToBlendSpace(new Vector2(localVelocity.x, localVelocity.z));

        m_animator.SetFloat(s_moveXHash, param.x, m_damping, Time.deltaTime);
        m_animator.SetFloat(s_moveZHash, param.y, m_damping, Time.deltaTime);
    }

    /// <summary>실제 속도(m/s)를 블렌드 트리 좌표로 구간별 매핑한다(앉기 중엔 앉기 속도 기준).</summary>
    private Vector2 NormalizeToBlendSpace(Vector2 velocity)
    {
        float speed = velocity.magnitude;
        if (speed < 0.01f)
            return Vector2.zero;

        bool crouching = m_crouch != null && m_crouch.IsCrouching;
        float walkSpeed = Mathf.Max(
            crouching ? m_movement.CrouchSpeed : m_movement.MoveSpeed,
            0.01f
        );
        float runSpeed = Mathf.Max(m_movement.SprintSpeed, walkSpeed + 0.01f);

        float t =
            speed <= walkSpeed
                ? speed / walkSpeed * k_walkParam
                : k_walkParam
                    + (speed - walkSpeed) / (runSpeed - walkSpeed) * (k_runParam - k_walkParam);

        return velocity / speed * t;
    }
}
