using UnityEngine;

/// <summary>
/// 이 플레이어가 동료를 구조·부활시킨 횟수를 집계한다(정산 칭호용).
/// </summary>
public class PlayerAssistCredit : MonoBehaviour
{
    private int m_rescueCount;

    public int RescueCount => m_rescueCount;

    /// <summary>구조 집계 — 리바이브·부활 키트 성공 지점(서버)에서 부른다. 서버(또는 오프라인) 전용.</summary>
    public void ServerCreditRescue()
    {
        m_rescueCount++;
    }

    /// <summary>라운드 사이 초기화. 서버(또는 오프라인) 전용 — 상점 진입 지점(ShopManager)에서 부른다.</summary>
    public void ServerResetRound()
    {
        m_rescueCount = 0;
    }
}
