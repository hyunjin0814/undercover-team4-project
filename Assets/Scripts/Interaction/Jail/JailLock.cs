using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 철창문 잠금 상태(잠김/열림)를 서버 권위로 동기화한다. JailZone과 같은 오브젝트에 둔다.
/// 탈옥 이벤트가 열고, 일정 시간 뒤 자동으로 다시 잠긴다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailLock : NetworkedManagerBase
{
    [Header("접근 지점 (비우면 자물쇠 자신의 위치)")]
    [Tooltip("침입자(#231)가 걸어와 서는 지점 — 배전반 앞에 둔다. 자물쇠 자신이 우리 안에 있으면 경로가 막힌다 (#415)")]
    [SerializeField] private Transform m_approachPoint;

    [Header("자동 재잠금")]
    [Tooltip("해킹으로 열린 뒤 자동으로 다시 잠기기까지의 시간(초). 경보등 탈옥 경보 시간보다 짧게 두지 말 것")]
    [Min(0f)]
    [SerializeField] private float m_relockSeconds = 10f;

    private readonly NetworkVariable<bool> m_locked = new NetworkVariable<bool>(true);

    private bool m_localLocked = true;

    private float m_relockAt;

    public bool IsLocked => IsSpawned ? m_locked.Value : m_localLocked;

    public Transform ApproachPoint => m_approachPoint != null ? m_approachPoint : transform;

    public event Action<bool> OnLockChanged;

    public event Action OnUnlockAttempt;

    public override void OnNetworkSpawn()
    {
        m_locked.OnValueChanged += HandleLockedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_locked.OnValueChanged -= HandleLockedChanged;
    }

    private void HandleLockedChanged(bool previous, bool current)
    {
        OnLockChanged?.Invoke(current);
    }

    /// <summary>해제 시도를 전 피어에 OnUnlockAttempt로 알린다. 서버(또는 오프라인) 전용.</summary>
    public void ServerAnnounceUnlockAttempt()
    {
        if (IsSpawned && !IsServer)
            return;

        OnUnlockAttempt?.Invoke();
        if (IsSpawned && IsServer)
            AnnounceUnlockAttemptClientRpc();
    }

    [ClientRpc]
    private void AnnounceUnlockAttemptClientRpc()
    {
        if (IsServer)
            return;

        OnUnlockAttempt?.Invoke();
    }

    /// <summary>개방 — 침입자의 배전반 해킹이 끝났을 때 탈출 이벤트가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerUnlock()
    {
        if (SetLocked(false))
            Debug.Log($"[유치장] 철창문 개방 — {m_relockSeconds}초 뒤 자동으로 닫힌다");
    }

    /// <summary>자동 복구 타이머로 다시 잠근다. 서버(또는 오프라인) 전용.</summary>
    public void ServerRelock()
    {
        if (SetLocked(true))
            Debug.Log("[유치장] 철창문 자동 재잠금");
    }

    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsLocked || Time.time < m_relockAt)
            return;

        ServerRelock();
    }

    private bool SetLocked(bool value)
    {
        if (IsSpawned && !IsServer)
            return false;

        if (IsLocked == value)
            return false;

        m_localLocked = value;

        if (!value)
            m_relockAt = Time.time + m_relockSeconds;

        if (IsSpawned && IsServer)
            m_locked.Value = value;
        else if (!IsSpawned)
        {
            OnLockChanged?.Invoke(value);
        }

        return true;
    }
}
