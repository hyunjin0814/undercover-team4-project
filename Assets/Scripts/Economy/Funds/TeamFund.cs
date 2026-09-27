using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 공용 자금 — 세션 내내 유지되는 상주 홀더.
/// 라운드 종료 정산으로 가산되고, 상점 구매(TrySpend)로만 차감된다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TeamFund : NetworkedManagerBase
{
    [Tooltip("세션 시작 시 초기 자금")]
    [Min(0)]
    [SerializeField] private int m_startingFund = 0;

    private readonly NetworkVariable<int> m_fund = new();

    public NetworkVariable<int> Fund => m_fund;
    public int Balance => m_fund.Value;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        m_fund.Value = SaveService.Pending?.TeamFund ?? m_startingFund;
    }

    /// <summary>라운드 종료 정산액을 팀 자금에 더한다(0 이하는 무시).</summary>
    public void AddSettlement(int amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.AddSettlement는 서버에서만", this);
            return;
        }
        if (amount <= 0) return;

        m_fund.Value = Mathf.Max(0, m_fund.Value + amount);
        Debug.Log($"[팀 자금] 라운드 정산 +{amount} → 잔액 {m_fund.Value}");
    }

    /// <summary>라운드 실패 시 자금을 세션 시작값으로 되돌린다. 서버(또는 오프라인) 전용.</summary>
    public void ResetToStarting()
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.ResetToStarting은 서버에서만", this);
            return;
        }

        int before = m_fund.Value;
        m_fund.Value = m_startingFund;
        Debug.Log($"[팀 자금] 라운드 실패로 초기화 — {before} → {m_fund.Value}");
    }

    /// <summary>자금을 차감한다. 잔액이 부족하면 차감하지 않고 false를 돌려준다.</summary>
    public bool TrySpend(int cost)
    {
        if (!IsServer)
        {
            Debug.LogWarning("TeamFund.TrySpend는 서버에서만", this);
            return false;
        }
        if (cost < 0 || m_fund.Value < cost) return false;

        m_fund.Value -= cost;
        Debug.Log($"[팀 자금] 차감 -{cost} → 잔액 {m_fund.Value}");
        return true;
    }
}
