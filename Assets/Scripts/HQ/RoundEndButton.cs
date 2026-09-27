using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 라운드 종료 버튼 — 목표 금액을 채우면 활성화되고, 누르면 라운드를 성공으로 조기 종료한다.
/// 활성 판정과 종료는 서버 권위이며 활성 여부만 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class RoundEndButton : NetworkBehaviour, IInteractable
{
    private RoundManager Round => App.Game.Round;

    private readonly NetworkVariable<bool> m_isArmedSynced = new(false);

    private bool m_isArmed;

    private bool m_warnedNoRound;

    public bool IsArmed => IsSpawned && !IsServer ? m_isArmedSynced.Value : m_isArmed;

    public event Action OnArmedChanged;

    private bool IsAuthority => !IsSpawned || IsServer;

    public override void OnNetworkSpawn()
    {
        m_isArmedSynced.OnValueChanged += HandleArmedSyncedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_isArmedSynced.OnValueChanged -= HandleArmedSyncedChanged;
    }

    private void HandleArmedSyncedChanged(bool previous, bool current) => OnArmedChanged?.Invoke();

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        if (!IsSpawned)
        {
            EndRound();
            return;
        }

        RequestEndRoundRpc();
    }

    /// <summary>목표를 채웠을 때만 누를 수 있다 — 조준 윤곽선도 그때만 켜진다.</summary>
    public bool CanInteract(GameObject interactor) => IsArmed;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RoundEnd;

    [Rpc(SendTo.Server)]
    private void RequestEndRoundRpc()
    {
        EndRound();
    }

    private void EndRound()
    {
        RoundManager round = Round;
        if (round == null)
        {
            Debug.LogWarning("RoundEndButton: RoundManager를 찾지 못해 라운드를 끝낼 수 없다", this);
            return;
        }

        if (!round.TryEndRoundManually())
            return;

        SetArmed(false);
    }

    private void Update()
    {
        if (!IsAuthority)
            return;

        RoundManager round = Round;
        if (round == null)
        {
            if (!m_warnedNoRound)
            {
                m_warnedNoRound = true;
                Debug.LogWarning("RoundEndButton: RoundManager를 찾지 못해 활성 판정이 돌지 않는다", this);
            }
            return;
        }

        SetArmed(round.Phase == RoundPhase.InProgress && round.IsTargetMet);
    }

    private void SetArmed(bool value)
    {
        if (m_isArmed == value)
            return;

        m_isArmed = value;

        if (IsSpawned && IsServer)
            m_isArmedSynced.Value = value;

        OnArmedChanged?.Invoke();
        Debug.Log($"[라운드 종료 버튼] {(value ? "활성화 — 목표 달성" : "비활성화")}");
    }
}
