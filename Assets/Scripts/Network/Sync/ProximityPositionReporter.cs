using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Vivox;
using UnityEngine;

/// <summary>
/// 로컬 플레이어가 움직일 때만 Vivox 근접 채널에 3D 위치를 보고하는 VivoxManager 부품.
/// </summary>
public class ProximityPositionReporter : MonoBehaviour
{
    [Tooltip("위치 보고 주기(초)")]
    [SerializeField]
    private float m_updateInterval = 0.1f;

    private string m_channelName;
    private bool m_reporting;
    private CancellationTokenSource m_cts;

    /// <summary>근접 채널 참가 완료 — 보고를 시작한다.</summary>
    public void StartReporting(string proximityChannelName)
    {
        m_channelName = proximityChannelName;
        m_reporting = true;
        Restart();
    }

    /// <summary>채널 이탈·비자발 드롭 — 보고를 멈춘다.</summary>
    public void StopReporting()
    {
        m_reporting = false;
        m_cts?.Cancel();
    }

    private void OnEnable()
    {
        if (m_reporting)
            Restart();
    }

    private void OnDisable() => m_cts?.Cancel();

    private void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
    }

    private void Restart()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
        m_cts = new CancellationTokenSource();
        PositionLoopAsync(m_cts.Token).Forget();
    }

    private async UniTaskVoid PositionLoopAsync(CancellationToken token)
    {
        Vector3 lastPos = Vector3.positiveInfinity;
        Quaternion lastRot = Quaternion.identity;
        NetworkObject local = null;

        while (!token.IsCancellationRequested)
        {
            if (local == null)
            {
                var nm = NetworkManager.Singleton;
                local = (nm != null && nm.IsClient) ? nm.LocalClient?.PlayerObject : null;
            }

            if (m_reporting && local != null)
            {
                var t = local.transform;
                bool moved =
                    (t.position - lastPos).sqrMagnitude > 0.0001f
                    || Quaternion.Angle(t.rotation, lastRot) > 0.5f;

                if (moved)
                {
                    VivoxService.Instance.Set3DPosition(local.gameObject, m_channelName);
                    lastPos = t.position;
                    lastRot = t.rotation;
                }
            }

            await UniTask.Delay(TimeSpan.FromSeconds(m_updateInterval), cancellationToken: token);
        }
    }
}
