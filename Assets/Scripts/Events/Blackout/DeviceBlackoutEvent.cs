using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 전자기기 먹통 돌발 이벤트 — 먹통 플래그를 서버 권위로 켜고 NetworkVariable로 동기화한다.
/// 스스로 풀리지 않고 본부 복구 단말의 ServerRecover로만 해제되며, 표현은 구독하는 쪽이 각자 담당한다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class DeviceBlackoutEvent : NetworkBehaviour, ISuddenEvent
{
    private const string k_recoveredNoticeKey = "Hud.Event.Notice.BlackoutRecovered";

    private readonly NetworkVariable<bool> m_blackoutSynced = new NetworkVariable<bool>();
    private bool m_blackout;

    public string DisplayName => "시스템 해킹";

    public string NoticeKey => "Hud.Event.Notice.Blackout";

    public bool IsActive => m_blackout;

    public bool IsCommsBlackout => IsSpawned && !IsServer ? m_blackoutSynced.Value : m_blackout;

    public event Action<bool> OnCommsBlackoutChanged;

    public override void OnNetworkSpawn()
    {
        m_blackoutSynced.OnValueChanged += HandleBlackoutSyncedChanged;

        if (!IsServer && m_blackoutSynced.Value)
            OnCommsBlackoutChanged?.Invoke(true);
    }

    public override void OnNetworkDespawn()
    {
        m_blackoutSynced.OnValueChanged -= HandleBlackoutSyncedChanged;
    }

    private void HandleBlackoutSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnCommsBlackoutChanged?.Invoke(current);
    }

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        SetBlackout(true);
    }

    public void ServerTick() { }

    /// <summary>먹통을 해제한다(본부 복구 단말). 실제로 풀렸을 때만 true. 서버(또는 오프라인) 전용.</summary>
    public bool ServerRecover()
    {
        if (IsSpawned && !IsServer)
            return false;

        if (!m_blackout)
            return false;

        Debug.Log("[돌발이벤트] 전자기기 먹통 — 본부 복구 단말로 해제");
        SetBlackout(false);

        App.Game.SuddenEvent?.Announce(DisplayName, k_recoveredNoticeKey);
        return true;
    }

    public void ServerReset()
    {
        SetBlackout(false);
    }

    private void SetBlackout(bool value)
    {
        if (m_blackout == value)
            return;

        m_blackout = value;
        if (IsSpawned && IsServer)
            m_blackoutSynced.Value = value;
        OnCommsBlackoutChanged?.Invoke(value);
    }
}
