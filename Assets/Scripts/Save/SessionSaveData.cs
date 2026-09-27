using System;

/// <summary>
/// 세이브 한 벌 — 상주 홀더 값과 개인 지갑을 JsonUtility로 직렬화한다.
/// 필드를 늘리면 k_version을 올릴 것.
/// </summary>
[Serializable]
public class SessionSaveData
{
    public const int k_version = 1;

    public int Version = k_version;

    public int Round = RoundProgress.k_firstRound;

    public int TeamFund;

    public int MapIndex;

    public string[] CarriedItems = Array.Empty<string>();

    public string[] Installables = Array.Empty<string>();

    public ShopSlotSaveEntry[] ShopSlots = Array.Empty<ShopSlotSaveEntry>();

    public PlayerSaveEntry[] Players = Array.Empty<PlayerSaveEntry>();
}

[Serializable]
public class ShopSlotSaveEntry
{
    public string Id = string.Empty;

    public bool Installable;

    public string Status = nameof(EShopSlotStatus.Available);
}

[Serializable]
public class PlayerSaveEntry
{
    public string PlayerId;
    public int Balance;
}
