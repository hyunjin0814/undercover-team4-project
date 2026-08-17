using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 라운드 종료 버튼 (#395) — 목표 금액을 채우면 활성화되고, 누르면 그 자리에서 라운드를 성공으로 끝낸다.
///
/// 목표를 채워도 라운드가 자동으로 끝나지 않는 것이 이 버튼의 존재 이유다: 더 벌지, 지금 끊고 챙길지를
/// 팀이 고른다. 제한시간까지 안 눌러도 목표를 넘겨 있으면 성공으로 끝나므로(RoundManager.Update),
/// 이 버튼은 "일찍 끊는" 수단이지 누르는 걸 잊으면 지는 함정이 아니다.
///
/// 서버 권위 — 활성 판정과 종료는 서버(또는 오프라인)에서만 돌고, 활성 여부만 동기화한다.
/// 불빛·소리 등 표시 연출은 각 클라의 로컬 연출이므로 OnArmedChanged를 구독해 붙이면 된다(이 이슈 범위 밖).
/// 씬 배치·모델·콜라이더는 Editor 작업이다 — 코드는 상호작용 경로까지만 만든다. (TipCallPhone과 같은 관례)
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class RoundEndButton : NetworkBehaviour, IInteractable
{
    private RoundManager Round => App.Game.Round;

    // 활성 여부만 동기화한다 — 목표 판정은 서버 안에서 끝난다 (TipCallPhone.m_isRingingSynced와 같은 구조)
    private readonly NetworkVariable<bool> m_isArmedSynced = new(false);

    // 서버·오프라인의 진실값 (NpcController.m_networkState와 동일 이중 구조)
    private bool m_isArmed;

    private bool m_warnedNoRound;

    /// <summary>지금 누를 수 있는가. 서버·오프라인은 진실값, 원격 피어는 동기화 값.</summary>
    public bool IsArmed => IsSpawned && !IsServer ? m_isArmedSynced.Value : m_isArmed;

    /// <summary>활성/비활성 전환 — 불빛·소리 연출이 구독할 훅. 전 피어에서 발행된다.</summary>
    public event Action OnArmedChanged;

    // 스폰 전(오프라인 단독 Play)이면 이 피어가 곧 권위다 — TipCallPhone과 동일
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

    // ---- 상호작용 ----

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        // 세션 밖(오프라인 단독 Play)에서는 RPC를 보낼 곳이 없다 — 이 피어가 곧 서버다
        if (!IsSpawned)
        {
            EndRound();
            return;
        }

        RequestEndRoundRpc();
    }

    /// <summary>목표를 채웠을 때만 누를 수 있다 — 조준 윤곽선도 그때만 켜진다. (#184)</summary>
    public bool CanInteract(GameObject interactor) => IsArmed;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RoundEnd;

    [Rpc(SendTo.Server)]
    private void RequestEndRoundRpc()
    {
        // CanInteract는 조준 피드백용 클라이언트 게이팅이라 RPC 직접 호출을 막지 못한다.
        // 실제 판정은 RoundManager.TryEndRoundManually가 다시 한다 — CCTVSwitcher와 같은 이유 (#362)
        EndRound();
    }

    // 라운드를 끝낸다 — 서버(또는 오프라인)에서만 돈다
    private void EndRound()
    {
        RoundManager round = Round;
        if (round == null)
        {
            Debug.LogWarning("RoundEndButton: RoundManager를 찾지 못해 라운드를 끝낼 수 없다", this);
            return;
        }

        // m_isArmed를 믿지 않고 RoundManager가 목표 달성을 다시 검증한다 — 활성 갱신이 한 프레임 늦거나
        // 조작된 RPC가 들어와도 미달 상태에서 끝나지 않는다. 거부되면 버튼은 그대로 둔다.
        if (!round.TryEndRoundManually())
            return;

        SetArmed(false);
    }

    // ---- 활성 판정 (서버 · 오프라인 전용) ----

    private void Update()
    {
        if (!IsAuthority)
            return;

        RoundManager round = Round;
        if (round == null)
        {
            // 여기서 조용히 return하면 버튼이 영영 안 켜지는데 아무 단서도 안 남는다 — 1회만 알린다
            if (!m_warnedNoRound)
            {
                m_warnedNoRound = true;
                Debug.LogWarning("RoundEndButton: RoundManager를 찾지 못해 활성 판정이 돌지 않는다", this);
            }
            return;
        }

        // 진행 중 + 목표 달성일 때만 켠다. 탈옥(#231)으로 누적 현상금이 목표 아래로 떨어지면 다시 꺼진다 —
        // 잡아둔 값이 아니라 "지금 유치장에 있는" 금액이 기준이기 때문이다.
        SetArmed(round.Phase == RoundPhase.InProgress && round.IsTargetMet);
    }

    private void SetArmed(bool value)
    {
        if (m_isArmed == value)
            return;

        m_isArmed = value;

        // 호스트는 동기화 변수 쓰기가 곧 자기 이벤트 발행이 아니다 — 아래에서 직접 발행한다
        if (IsSpawned && IsServer)
            m_isArmedSynced.Value = value;

        OnArmedChanged?.Invoke();
        Debug.Log($"[라운드 종료 버튼] {(value ? "활성화 — 목표 달성" : "비활성화")}");
    }
}
