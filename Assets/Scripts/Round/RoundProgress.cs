using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션 내 라운드 번호를 들고 있는 상주 홀더 — 할당량 난이도가 이 값을 따른다.
/// 성공 종료 시 증가하고 실패 시 1로 되돌린다. 오프라인에서는 없으므로 null이면 1라운드로 취급한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class RoundProgress : NetworkedManagerBase
{
    public const int k_firstRound = 1;

    private readonly NetworkVariable<int> m_round = new(k_firstRound);

    public int Current => m_round.Value;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        m_round.Value = SaveService.Pending?.Round ?? k_firstRound;
    }

    /// <summary>다음 라운드로 넘긴다 — 라운드 성공 종료 시 RoundEndResetter가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void Advance()
    {
        if (!IsServer)
        {
            Debug.LogWarning("RoundProgress.Advance는 서버에서만", this);
            return;
        }

        m_round.Value++;
        Debug.Log($"[라운드 진행도] 다음 라운드 — {m_round.Value}라운드");
    }

    /// <summary>진행도를 첫 라운드로 되돌린다 — 라운드 실패로 판이 끝났을 때. 서버(또는 오프라인) 전용.</summary>
    public void ResetToFirst()
    {
        if (!IsServer)
        {
            Debug.LogWarning("RoundProgress.ResetToFirst는 서버에서만", this);
            return;
        }

        int before = m_round.Value;
        m_round.Value = k_firstRound;
        Debug.Log($"[라운드 진행도] 라운드 실패로 초기화 — {before} → {m_round.Value}라운드");
    }
}
