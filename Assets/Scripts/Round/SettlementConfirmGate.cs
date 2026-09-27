using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 각 피어의 정산 확인을 서버가 모아 전원이 확인하면 AllConfirmed를 세운다.
/// 오프라인에서는 로컬 확인 하나를 1/1로 센다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SettlementConfirmGate : NetworkedManagerBase
{
    private readonly NetworkVariable<int> m_confirmedCountSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_expectedCountSynced = new NetworkVariable<int>();

    private readonly HashSet<ulong> m_expected = new HashSet<ulong>();
    private readonly HashSet<ulong> m_confirmed = new HashSet<ulong>();

    private bool m_offlineConfirmed;

    public event Action OnCountsChanged;

    public int ConfirmedCount =>
        IsSpawned ? m_confirmedCountSynced.Value
        : m_offlineConfirmed ? 1
        : 0;

    public int ExpectedCount => IsSpawned ? m_expectedCountSynced.Value : 1;

    public bool AllConfirmed => ExpectedCount > 0 && ConfirmedCount >= ExpectedCount;

    public override void OnNetworkSpawn()
    {
        m_confirmedCountSynced.OnValueChanged += HandleCountSynced;
        m_expectedCountSynced.OnValueChanged += HandleCountSynced;

        if (!IsServer)
            return;

        m_expected.Clear();
        m_confirmed.Clear();

        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            m_expected.Add(clientId);

        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        PublishCounts();
    }

    public override void OnNetworkDespawn()
    {
        m_confirmedCountSynced.OnValueChanged -= HandleCountSynced;
        m_expectedCountSynced.OnValueChanged -= HandleCountSynced;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    /// <summary>이 피어의 정산 확인을 서버에 보고한다.</summary>
    public void ReportSelfConfirmed()
    {
        if (!IsSpawned)
        {
            if (m_offlineConfirmed)
                return;
            m_offlineConfirmed = true;
            OnCountsChanged?.Invoke();
            return;
        }

        ReportConfirmedRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportConfirmedRpc(RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;

        m_expected.Add(clientId);
        m_confirmed.Add(clientId);
        PublishCounts();
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        m_expected.Remove(clientId);
        m_confirmed.Remove(clientId);
        PublishCounts();
    }

    private void PublishCounts()
    {
        m_confirmedCountSynced.Value = m_confirmed.Count;
        m_expectedCountSynced.Value = m_expected.Count;
    }

    private void HandleCountSynced(int previous, int current) => OnCountsChanged?.Invoke();
}
