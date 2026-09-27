using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 안개 라운드 날씨 — 활성 플래그를 서버 권위로 켜고 NetworkVariable로 동기화한다.
/// 준비 단계에 뽑혀 라운드 끝까지 유지되며, 표현은 FogView가 맡는다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class FogEvent : NetworkBehaviour, IRoundWeather
{
    private readonly NetworkVariable<bool> m_fogSynced = new NetworkVariable<bool>();
    private bool m_fog;

    public string DisplayName => "안개";

    public WeatherKind Kind => WeatherKind.Fog;

    public bool IsActive => m_fog;

    public bool AnnounceOnBegin => false;

    public bool IsFog => IsSpawned && !IsServer ? m_fogSynced.Value : m_fog;

    public event Action<bool> OnFogChanged;

    public override void OnNetworkSpawn()
    {
        m_fogSynced.OnValueChanged += HandleFogSyncedChanged;

        if (!IsServer && m_fogSynced.Value)
            OnFogChanged?.Invoke(true);
    }

    public override void OnNetworkDespawn()
    {
        m_fogSynced.OnValueChanged -= HandleFogSyncedChanged;
    }

    private void HandleFogSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnFogChanged?.Invoke(current);
    }

    public bool CanTrigger() => true;

    public void ServerBegin() => SetFog(true);

    public void ServerTick() { }

    public void ServerReset()
    {
        SetFog(false);
    }

    private void SetFog(bool value)
    {
        if (m_fog == value)
            return;

        m_fog = value;
        if (IsSpawned && IsServer)
            m_fogSynced.Value = value;
        OnFogChanged?.Invoke(value);
    }
}
