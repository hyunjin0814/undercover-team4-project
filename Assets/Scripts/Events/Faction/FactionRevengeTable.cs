using UnityEngine;

/// <summary>
/// 라운드별 세력 복수대 인원 표 SO. 표 끝을 넘으면 마지막 행을 유지한다.
/// </summary>
[CreateAssetMenu(fileName = "FactionRevengeTable", menuName = "Undercover/Events/Faction Revenge Table")]
public class FactionRevengeTable : ScriptableObject
{
    [Tooltip("라운드 순서대로 나열한 복수대 인원. 첫 항목 = 1라운드, 마지막 항목 = 그 이후 모든 라운드")]
    [Min(1)]
    [SerializeField] private int[] m_memberCounts = { 2, 3, 4 };

    public bool HasRows => m_memberCounts != null && m_memberCounts.Length > 0;

    /// <summary>N라운드의 복수대 인원. 표가 비었거나 값이 0 이하면 <paramref name="fallback"/>.</summary>
    public int GetMemberCount(int round, int fallback)
    {
        if (!HasRows) return fallback;

        int index = Mathf.Clamp(round - RoundProgress.k_firstRound, 0, m_memberCounts.Length - 1);
        int count = m_memberCounts[index];
        return count > 0 ? count : fallback;
    }
}
