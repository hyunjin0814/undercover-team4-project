using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감정표현 재생 상태를 서버 권위 NetworkVariable로 동기화한다.
/// 이동 취소는 오너가 판정하고, 서버는 무력화·앉기·공중·업힘만 본다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerEmote : NetworkBehaviour
{
    public const sbyte k_none = -1;

    [Tooltip("감정표현 목록 — 모든 피어가 같은 에셋을 봐야 한다")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    private readonly NetworkVariable<sbyte> m_activeEmote = new(
        k_none,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private PlayerCarrier m_carrier;

    private float m_autoStopTime;

    public EmoteCatalog Catalog => m_catalog;
    public sbyte ActiveEmote => m_activeEmote.Value;
    public bool IsEmoting => m_activeEmote.Value != k_none;

    public event Action<sbyte> OnActiveEmoteChanged;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_carrier = GetComponent<PlayerCarrier>();
    }

    public override void OnNetworkSpawn()
    {
        m_activeEmote.OnValueChanged += HandleActiveEmoteChanged;

        if (IsEmoting)
            OnActiveEmoteChanged?.Invoke(m_activeEmote.Value);

        if (IsServer)
            m_incapacitation.OnIncapacitatedChanged += HandleIncapacitatedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_activeEmote.OnValueChanged -= HandleActiveEmoteChanged;

        if (IsServer && m_incapacitation != null)
            m_incapacitation.OnIncapacitatedChanged -= HandleIncapacitatedChanged;
    }

    /// <summary>감정표현 발동을 요청한다 — 오너가 부른다. 서버가 조건을 보고 결정한다.</summary>
    public void RequestEmote(int index)
    {
        if (!IsOwner)
            return;

        RequestEmoteServerRpc((sbyte)index);
    }

    /// <summary>재생 중인 감정표현을 끊는다 — 오너가 부른다(이동·공격 등).</summary>
    public void CancelEmote()
    {
        if (!IsOwner)
            return;

        CancelEmoteServerRpc();
    }

    [ServerRpc]
    private void RequestEmoteServerRpc(sbyte index)
    {
        if (m_catalog == null || !m_catalog.IsValidIndex(index))
            return;

        if (!CanStartEmote())
            return;

        m_activeEmote.Value = index;

        float duration = m_catalog.Get(index).DurationSeconds;
        m_autoStopTime = duration > 0f ? Time.time + duration : 0f;
    }

    [ServerRpc]
    private void CancelEmoteServerRpc() => ServerStopEmote();

    private bool CanStartEmote()
    {
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false;

        if (m_crouch != null && m_crouch.IsCrouching)
            return false;

        if (m_jump != null && m_jump.IsAirborne)
            return false;

        if (m_carrier != null && m_carrier.IsBeingCarried)
            return false;

        return true;
    }

    private void Update()
    {
        if (!IsServer || !IsEmoting)
            return;

        if (m_autoStopTime > 0f && Time.time >= m_autoStopTime)
        {
            ServerStopEmote();
            return;
        }

        if (!CanStartEmote())
            ServerStopEmote();
    }

    private void HandleIncapacitatedChanged(bool incapacitated)
    {
        if (incapacitated)
            ServerStopEmote();
    }

    private void ServerStopEmote()
    {
        if (!IsServer || !IsEmoting)
            return;

        m_activeEmote.Value = k_none;
        m_autoStopTime = 0f;
    }

    private void HandleActiveEmoteChanged(sbyte previous, sbyte current)
    {
        OnActiveEmoteChanged?.Invoke(current);
    }
}
