using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 각 피어의 씬 준비 보고를 서버가 모아 전원이 준비되면 게이트를 연다. 시간 초과 시 경고 후 연다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SceneReadyGate : NetworkedManagerBase
{
    [Tooltip("전원 보고를 기다리는 상한(초) — 넘으면 경고 후 남은 인원으로 연다")]
    [SerializeField] private float m_readyTimeoutSeconds = 30f;

    private readonly NetworkVariable<bool> m_openSynced = new NetworkVariable<bool>();

    private readonly NetworkVariable<int> m_readyCountSynced = new NetworkVariable<int>();
    private readonly NetworkVariable<int> m_expectedCountSynced = new NetworkVariable<int>();

    private readonly HashSet<ulong> m_expected = new HashSet<ulong>();
    private readonly HashSet<ulong> m_ready = new HashSet<ulong>();

    private float m_deadline;

    public bool IsOpen => m_openSynced.Value;

    public int ReadyCount => m_readyCountSynced.Value;

    public int ExpectedCount => m_expectedCountSynced.Value;

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        m_expected.Clear();
        m_ready.Clear();
        m_openSynced.Value = false;

        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
            m_expected.Add(clientId);

        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        m_deadline = Time.realtimeSinceStartup + m_readyTimeoutSeconds;
        PublishAndEvaluate();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    /// <summary>이 피어의 씬 준비 완료를 서버에 보고한다.</summary>
    public void ReportSelfReady()
    {
        if (!IsSpawned)
            return;

        ReportReadyRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportReadyRpc(RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;

        m_expected.Add(clientId);
        m_ready.Add(clientId);
        PublishAndEvaluate();
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        m_expected.Remove(clientId);
        m_ready.Remove(clientId);
        PublishAndEvaluate();
    }

    private void Update()
    {
        if (!IsSpawned || !IsServer || m_openSynced.Value)
            return;

        if (Time.realtimeSinceStartup < m_deadline)
            return;

        Debug.LogWarning(
            $"[준비] 전원 준비 완료를 {m_readyTimeoutSeconds:0.#}초 내에 받지 못했다 "
                + $"({m_ready.Count}/{m_expected.Count}명) — 남은 인원으로 진행한다",
            this
        );
        m_openSynced.Value = true;
    }

    private void PublishAndEvaluate()
    {
        m_readyCountSynced.Value = m_ready.Count;
        m_expectedCountSynced.Value = m_expected.Count;

        if (m_openSynced.Value || m_ready.Count < m_expected.Count)
            return;

        Debug.Log($"[준비] 전원 준비 완료 — {m_ready.Count}명");
        m_openSynced.Value = true;
    }
}
