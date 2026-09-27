using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 라운드 종료 시각(서버 시계 기준)을 NetworkVariable로 공유해 각 피어가 남은 시간을 계산하게 한다(표시 전용).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class RoundTimerSync : NetworkBehaviour
{
    private const double k_notRunning = 0d;

    private RoundManager Round => App.Game.Round;

    private readonly NetworkVariable<double> m_endServerTime = new NetworkVariable<double>(
        k_notRunning
    );

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        m_endServerTime.Value = k_notRunning;

        if (Round == null)
        {
            Debug.LogWarning(
                "RoundTimerSync: RoundManager를 찾지 못해 타이머를 동기화할 수 없다",
                this
            );
            return;
        }

        Round.OnRoundStarted += HandleRoundStarted;
        Round.OnRoundEnded += HandleRoundEnded;

        if (Round.Phase == RoundPhase.InProgress)
            HandleRoundStarted();
    }

    public override void OnNetworkDespawn()
    {
        if (Round == null)
            return;

        Round.OnRoundStarted -= HandleRoundStarted;
        Round.OnRoundEnded -= HandleRoundEnded;
    }

    private void HandleRoundStarted()
    {
        if (float.IsPositiveInfinity(Round.RemainingSeconds))
        {
            m_endServerTime.Value = k_notRunning;
            return;
        }

        m_endServerTime.Value = NetworkManager.ServerTime.Time + Round.RemainingSeconds;
    }

    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        m_endServerTime.Value = k_notRunning;
    }

    /// <summary>서버 시계 기준 남은 시간(초)을 계산한다. 타이머가 돌지 않으면 false.</summary>
    public bool TryGetRemainingSeconds(out float seconds)
    {
        seconds = 0f;
        if (!IsSpawned || m_endServerTime.Value <= k_notRunning)
            return false;

        seconds = Mathf.Max(0f, (float)(m_endServerTime.Value - NetworkManager.ServerTime.Time));
        return true;
    }
}
