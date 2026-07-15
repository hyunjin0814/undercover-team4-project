using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 전자기기 먹통 (돌발 이벤트 · 전역) — 도시 인프라 장애로 시야·통신이 제한된다. (GDD 6-4/4-4/7-4, #106)
/// 먹통 플래그를 <b>스스로 소유</b>해 서버 권위로 켜고 끄며, NetworkVariable로 전 클라에 동기화한다.
/// 실제 표현(통신 차단·시야 오버레이)은 이 플래그를 구독하는 <see cref="DeviceBlackoutView"/>가 전 클라에서 담당한다.
/// 시간이 지나면 자동으로 해제된다.
///
/// 서버 권위 — 발생·해제 판정은 서버(또는 오프라인)에서만. 프레임워크(<see cref="SuddenEventManager"/>)가
/// ServerBegin/Tick/Reset을 서버에서만 부르므로 이 안에서는 권위를 다시 검사하지 않는다. (#56)
///
/// 전역 효과라 스폰물 대신 동기화 플래그로 전파한다 — 이 상태를 매니저가 아니라 이벤트가 들고 있어야
/// 프레임워크가 "먹통"이라는 이벤트의 존재를 몰라도 된다. NetworkVariable을 쓰려면 NetworkBehaviour여야 하는데,
/// 매니저가 요구하는 NetworkObject가 같은 오브젝트에 이미 있으므로 여기에 얹으면 된다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class DeviceBlackoutEvent : NetworkBehaviour, ISuddenEvent
{
    [Header("지속 시간(초)")]
    [Tooltip("먹통이 유지되는 시간 — 지나면 시야·통신이 자동 복구된다")]
    [SerializeField]
    private float m_durationSeconds = 12f;

    // 먹통 전역 상태 — 서버만 쓰고 모든 클라가 읽는다. (PlayerData.m_syncedHp와 동일 이중 구조)
    private readonly NetworkVariable<bool> m_blackoutSynced = new NetworkVariable<bool>();
    private bool m_blackout; // 서버·오프라인의 진실값 (비네트워크 Play 폴백)

    private float m_endTime;

    public string DisplayName => "전자기기 먹통";

    // 먹통이 켜져 있는 동안이 곧 이벤트 진행 중 — 매니저는 이 값이 false가 될 때까지 재발생시키지 않는다.
    // (매니저가 IsActive를 읽는 것은 서버·오프라인에서뿐이므로 서버 진실값을 그대로 준다)
    public bool IsActive => m_blackout;

    /// <summary>통신·시야 먹통이 활성인지 — 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정.</summary>
    public bool IsCommsBlackout => IsSpawned && !IsServer ? m_blackoutSynced.Value : m_blackout;

    /// <summary>먹통 상태가 바뀔 때 발행 — 통신 차단·시야 오버레이 등 표현 계층이 구독한다. (#67 무전·시야 연동)</summary>
    public event Action<bool> OnCommsBlackoutChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버의 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_blackoutSynced.OnValueChanged += HandleBlackoutSyncedChanged;

        // 늦게 접속한 클라: 이미 먹통이 진행 중이면 현재 상태를 즉시 반영한다.
        if (!IsServer && m_blackoutSynced.Value)
            OnCommsBlackoutChanged?.Invoke(true);
    }

    public override void OnNetworkDespawn()
    {
        m_blackoutSynced.OnValueChanged -= HandleBlackoutSyncedChanged;
    }

    // 서버(호스트 포함)는 SetBlackout에서 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleBlackoutSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnCommsBlackoutChanged?.Invoke(current);
    }

    // 전역 이벤트라 특별한 선행 조건이 없다 — 프레임워크가 라운드 진행 중에만 호출한다
    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        m_endTime = Time.time + m_durationSeconds;
        SetBlackout(true);
    }

    public void ServerTick()
    {
        if (!m_blackout)
            return;

        if (Time.time >= m_endTime)
        {
            Debug.Log("[돌발이벤트] 전자기기 먹통 — 시간 경과로 복구");
            SetBlackout(false);
        }
    }

    public void ServerReset()
    {
        SetBlackout(false);
    }

    // 먹통 상태 설정 — 서버(또는 오프라인)에서만 호출된다.
    // 동기화 변수와 로컬 진실값을 함께 갱신하고, 표현 계층(통신·시야)에 이벤트로 알린다.
    private void SetBlackout(bool value)
    {
        if (m_blackout == value)
            return; // 중복 트리거·중복 해제 무시

        m_blackout = value;
        if (IsSpawned && IsServer)
            m_blackoutSynced.Value = value; // OnValueChanged로 원격 클라에 중계
        OnCommsBlackoutChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 위 동기화 콜백이 담당)
    }
}
