using UnityEngine;

/// <summary>
/// 라운드별 할당량(목표 금액) 표 SO. 표 끝을 넘으면 마지막 값을 유지한다.
/// </summary>
[CreateAssetMenu(fileName = "RoundQuotaTable", menuName = "Undercover/Round/Quota Table")]
public class RoundQuotaTable : ScriptableObject
{
    [Tooltip("라운드 순서대로 나열한 할당량(목표 금액). 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드")]
    [Min(1)]
    [SerializeField] private int[] m_quotas = { 30000, 45000, 60000 };

    public bool HasRows => m_quotas != null && m_quotas.Length > 0;

    /// <summary>N라운드의 할당량을 돌려준다. 없거나 0 이하면 fallback.</summary>
    public int GetQuota(int round, int fallback)
    {
        if (!HasRows) return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_quotas.Length - 1);
        int quota = m_quotas[index];
        return quota > 0 ? quota : fallback;
    }
}
