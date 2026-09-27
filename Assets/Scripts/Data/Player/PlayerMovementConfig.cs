using UnityEngine;

/// <summary>
/// 모든 플레이어가 공유하는 이동 튜닝 수치를 모은 SO.
/// </summary>
[CreateAssetMenu(fileName = "PlayerMovementConfig", menuName = "Scriptable Objects/PlayerMovementConfig")]
public class PlayerMovementConfig : ScriptableObject
{
    [Header("이동 속도(m/s)")]
    [SerializeField] private float m_moveSpeed = 5f;
    [SerializeField] private float m_sprintSpeed = 8f;
    [SerializeField] private float m_crouchSpeed = 2.5f;

    [Header("중력")]
    [Tooltip("중력 가속도(m/s²) — 음수")]
    [SerializeField] private float m_gravity = -9.81f;

    [Header("넉백 (폭발 등 외력)")]
    [Tooltip("넉백 속도가 잦아드는 감쇠율(1/초) — 클수록 빨리 멈춘다")]
    [SerializeField] private float m_knockbackDamping = 4f;

    [Header("날씨 효과 (눈)")]
    [Tooltip("기본 이동 마찰 계수 (보간 속도)")]
    [SerializeField] private float m_defaultFriction = 15f;
    [Tooltip("눈 올 때의 마찰 계수 (미끄러짐)")]
    [SerializeField] private float m_snowFriction = 0.5f;

    public float MoveSpeed => m_moveSpeed;
    public float SprintSpeed => m_sprintSpeed;
    public float CrouchSpeed => m_crouchSpeed;
    public float Gravity => m_gravity;
    public float KnockbackDamping => m_knockbackDamping;
    public float DefaultFriction => m_defaultFriction;
    public float SnowFriction => m_snowFriction;
}
