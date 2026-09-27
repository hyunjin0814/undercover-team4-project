using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 앉기 상태를 서버 권위로 관리한다 — 오너는 홀드 입력을 전달하고 서버가 NetworkVariable로 전파한다.
/// 원격 피어에서도 CharacterController 높이를 함께 줄인다.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerCrouch : NetworkBehaviour
{
    public const float k_blendDuration = 0.12f;

    [Header("앉기")]
    [Tooltip("앉았을 때 CharacterController 높이(m). 서기 높이는 프리팹 값에서 캡처한다.")]
    [SerializeField]
    private float m_crouchHeight = 1.2f;

    private readonly NetworkVariable<bool> m_isCrouchingSynced = new NetworkVariable<bool>();
    private bool m_isCrouching;
    private bool m_crouchRequested;

    private readonly NetworkVariable<bool> m_isCrouchRequestedSynced = new NetworkVariable<bool>();
    private bool m_isCrouchRequested;

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation;
    private PlayerJump m_jump;
    private float m_standHeight;
    private float m_standCenterY;
    private float m_crouchBlend;

    public bool IsCrouching => IsSpawned && !IsServer ? m_isCrouchingSynced.Value : m_isCrouching;

    public bool IsCrouchRequested =>
        IsSpawned && !IsServer && !IsOwner ? m_isCrouchRequestedSynced.Value : m_isCrouchRequested;

    public float CrouchBlend => m_crouchBlend;

    public float HeadDrop => m_standHeight - m_controller.height;

    public float HeadDropRate => (m_standHeight - m_crouchHeight) / k_blendDuration;

    public event Action<bool> OnCrouchChanged;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_jump = GetComponent<PlayerJump>();

        m_standHeight = m_controller.height;
        m_standCenterY = m_controller.center.y;
    }

    public override void OnNetworkSpawn()
    {
        m_isCrouchingSynced.OnValueChanged += HandleSyncedChanged;

        m_crouchBlend = IsCrouchRequested ? 1f : 0f;
        ApplyColliderHeight();

        if (IsOwner)
        {
            m_inputHandler.OnCrouchChanged += HandleCrouchInput;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_isCrouchingSynced.OnValueChanged -= HandleSyncedChanged;

        if (IsOwner)
        {
            m_inputHandler.OnCrouchChanged -= HandleCrouchInput;
        }
    }

    private void HandleSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnCrouchChanged?.Invoke(current);
    }

    private void HandleCrouchInput(bool pressed)
    {
        m_isCrouchRequested = pressed;
        RequestCrouchServerRpc(pressed);
    }

    [ServerRpc]
    private void RequestCrouchServerRpc(bool pressed)
    {
        m_crouchRequested = pressed;
        m_isCrouchRequested = pressed;
        m_isCrouchRequestedSynced.Value = pressed;
    }

    private void Update()
    {
        if (!IsSpawned || IsServer)
        {
            UpdateServerState();
        }

        UpdateBlend();
    }

    private void UpdateServerState()
    {
        bool desired =
            m_crouchRequested
            && !(m_incapacitation != null && m_incapacitation.IsIncapacitated)
            && !(m_jump != null && m_jump.IsAirborne);
        if (m_isCrouching == desired)
            return;

        m_isCrouching = desired;
        if (IsSpawned && IsServer)
            m_isCrouchingSynced.Value = desired;
        OnCrouchChanged?.Invoke(desired);
    }

    private void UpdateBlend()
    {
        float target = IsCrouchRequested ? 1f : 0f;
        if (Mathf.Approximately(m_crouchBlend, target))
            return;

        m_crouchBlend = Mathf.MoveTowards(m_crouchBlend, target, Time.deltaTime / k_blendDuration);
        ApplyColliderHeight();
    }

    private void ApplyColliderHeight()
    {
        float height = Mathf.Lerp(m_standHeight, m_crouchHeight, m_crouchBlend);
        m_controller.height = height;

        Vector3 center = m_controller.center;
        center.y = m_standCenterY - (m_standHeight - height) * 0.5f;
        m_controller.center = center;
    }
}
