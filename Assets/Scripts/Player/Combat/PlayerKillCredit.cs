using Unity.Netcode;

/// <summary>
/// 이 플레이어가 막타로 처치한 횟수를 서버 권위로 집계하고 오너에게 처치 알림을 보낸다.
/// </summary>
public class PlayerKillCredit : NetworkBehaviour
{
    private int m_killCount;
    private int m_innocentKillCount;

    public int KillCount => m_killCount;

    public int InnocentKillCount => m_innocentKillCount;

    /// <summary>처치 집계 — 사망 판정 지점(서버)에서 가해자 쪽에 부른다. 서버(또는 오프라인) 전용.</summary>
    public void ServerCreditKill(
        string victimName,
        bool friendlyFire,
        bool isInnocentCivilian = false
    )
    {
        if (IsSpawned && !IsServer)
            return;

        m_killCount++;
        if (isInnocentCivilian)
            m_innocentKillCount++;
        NotifyOwner(victimName, friendlyFire);
    }

    /// <summary>라운드 사이 초기화. 서버(또는 오프라인) 전용 — 상점 진입 지점(ShopManager)에서 부른다.</summary>
    public void ServerResetRound()
    {
        if (IsSpawned && !IsServer)
            return;

        m_killCount = 0;
        m_innocentKillCount = 0;
    }

    private void NotifyOwner(string victimName, bool friendlyFire)
    {
        if (!IsSpawned)
        {
            ApplyKillMarker(victimName, friendlyFire);
            return;
        }

        NotifyKillRpc(victimName, friendlyFire);
    }

    [Rpc(SendTo.Owner)]
    private void NotifyKillRpc(string victimName, bool friendlyFire) =>
        ApplyKillMarker(victimName, friendlyFire);

    private static void ApplyKillMarker(string victimName, bool friendlyFire)
    {
        App.UI.Crosshair?.ShowKill(victimName, friendlyFire);
        App.Sound?.PlaySfx2D(friendlyFire ? EAudioClip.KillFriendly : EAudioClip.KillConfirm);
    }
}
