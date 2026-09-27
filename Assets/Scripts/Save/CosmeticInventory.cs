using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 계정이 뽑아서 얻은 치장과 뽑기 토큰 — 정본은 Cloud Save, PlayerPrefs는 계정별 캐시다.
/// 기본 지급 세트는 카탈로그가 정하며 IsOwned가 합쳐 판정한다.
/// </summary>
public static class CosmeticInventory
{
    private const string k_ownedKeyPrefix = "cosmetic.owned.";
    private const string k_tokenKeyPrefix = "cosmetic.tokens.";
    private const string k_localAccount = "local";

    private const int k_bitsPerChunk = 32;

    private static readonly int[][] s_owned = new int[
        Enum.GetValues(typeof(EAccessorySlot)).Length
    ][];
    private static string s_account = k_localAccount;
    private static int s_tokens;

    public static event Action OnOwnedChanged;

    public static event Action OnTokensChanged;

    public static int Tokens => s_tokens;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        OnOwnedChanged = null;
        OnTokensChanged = null;

        s_pendingReward = 0;
        s_account = k_localAccount;
        LoadAccount();
    }

    /// <summary>그 치장을 쓸 수 있는지(기본 지급 또는 획득) 판정한다.</summary>
    public static bool IsOwned(AccessoryCatalog catalog, EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return true;

        if (catalog != null && catalog.IsDefaultOwned(slot, index))
            return true;

        return HasBit(slot, index);
    }

    /// <summary>뽑아서 얻은 치장을 담는다. 새로 얻었으면 true.</summary>
    public static bool Grant(EAccessorySlot slot, int index)
    {
        if (index <= 0 || HasBit(slot, index))
            return false;

        SetBit(slot, index);
        SaveOwned(slot);
        OnOwnedChanged?.Invoke();
        return true;
    }

    /// <summary>보유하지 않은 치장을 입고 있으면 벗긴다.</summary>
    public static void SanitizeEquipped(AccessoryCatalog catalog)
    {
        if (catalog == null)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int index = CosmeticLoadout.GetAccessory(slot);
            if (index <= 0 || IsOwned(catalog, slot, index))
                continue;

            Debug.Log($"[치장] 가지지 않은 {slot} {index}번을 입고 있어 벗긴다 (#818 D)");
            CosmeticLoadout.SetAccessory(slot, 0);
        }
    }

    private static int s_pendingReward;

    /// <summary>축하할 지급분을 적어 둔다 — 상점에 들어갈 때까지 쌓인다.</summary>
    public static void QueueRewardNotice(int count)
    {
        if (count > 0)
            s_pendingReward += count;
    }

    /// <summary>적어 둔 지급분을 가져가며 비운다 — 두 번 축하하지 않는다.</summary>
    public static int ClaimRewardNotice()
    {
        int claimed = s_pendingReward;
        s_pendingReward = 0;
        return claimed;
    }

    /// <summary>토큰을 더한다 — 라운드 클리어 지급. 0 이하는 무시한다.</summary>
    public static void AddTokens(int count)
    {
        if (count <= 0)
            return;

        s_tokens += count;
        SaveTokens();
        OnTokensChanged?.Invoke();
    }

    /// <summary>토큰 1개를 쓴다 — 없으면 false고 아무것도 바뀌지 않는다.</summary>
    public static bool TrySpendToken()
    {
        if (s_tokens <= 0)
            return false;

        s_tokens--;
        SaveTokens();
        OnTokensChanged?.Invoke();
        return true;
    }

    /// <summary>계정 자리를 갈아탄다 — 캐시에서 그 계정 것을 다시 읽는다. (CosmeticsSaveService)</summary>
    public static void UseAccount(string accountId)
    {
        string next = string.IsNullOrWhiteSpace(accountId) ? k_localAccount : accountId;
        if (s_account == next)
            return;

        s_account = next;
        LoadAccount();
    }

    /// <summary>계정에 올릴 한 벌을 뜬다. (CosmeticsSaveService)</summary>
    public static List<CosmeticSlotOwnership> Capture()
    {
        var captured = new List<CosmeticSlotOwnership>();
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int[] bits = s_owned[(int)slot];
            if (bits == null || bits.Length == 0)
                continue;

            captured.Add(
                new CosmeticSlotOwnership { Slot = (int)slot, Bits = (int[])bits.Clone() }
            );
        }

        return captured;
    }

    /// <summary>클라우드에서 받은 보유 목록과 토큰으로 덮어쓰고 캐시에도 남긴다.</summary>
    public static void Apply(IList<CosmeticSlotOwnership> owned, int tokens)
    {
        int slotCount = s_owned.Length;
        for (int i = 0; i < slotCount; i++)
            s_owned[i] = null;

        if (owned != null)
            foreach (CosmeticSlotOwnership entry in owned)
            {
                if (entry == null || entry.Slot < 0 || entry.Slot >= slotCount)
                    continue;

                s_owned[entry.Slot] = entry.Bits == null ? null : (int[])entry.Bits.Clone();
            }

        s_tokens = Mathf.Max(0, tokens);

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            SaveOwned(slot);

        SaveTokens();
        OnOwnedChanged?.Invoke();
        OnTokensChanged?.Invoke();
    }

    private static bool HasBit(EAccessorySlot slot, int index)
    {
        int[] bits = s_owned[(int)slot];
        int chunk = index / k_bitsPerChunk;
        if (bits == null || chunk >= bits.Length)
            return false;

        return (bits[chunk] & (1 << (index % k_bitsPerChunk))) != 0;
    }

    private static void SetBit(EAccessorySlot slot, int index)
    {
        int chunk = index / k_bitsPerChunk;
        int[] bits = s_owned[(int)slot];

        if (bits == null)
            bits = new int[chunk + 1];
        else if (chunk >= bits.Length)
            Array.Resize(ref bits, chunk + 1);

        bits[chunk] |= 1 << (index % k_bitsPerChunk);
        s_owned[(int)slot] = bits;
    }

    private static void LoadAccount()
    {
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            s_owned[(int)slot] = ParseBits(PlayerPrefs.GetString(OwnedKey(slot), string.Empty));

        s_tokens = Mathf.Max(0, PlayerPrefs.GetInt(TokenKey(), 0));

        OnOwnedChanged?.Invoke();
        OnTokensChanged?.Invoke();
    }

    private static int[] ParseBits(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;

        string[] parts = stored.Split(',');
        var bits = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], out bits[i]);

        return bits;
    }

    private static void SaveOwned(EAccessorySlot slot)
    {
        int[] bits = s_owned[(int)slot];
        PlayerPrefs.SetString(
            OwnedKey(slot),
            bits == null || bits.Length == 0
                ? string.Empty
                : string.Join(",", Array.ConvertAll(bits, b => b.ToString()))
        );
    }

    private static void SaveTokens() => PlayerPrefs.SetInt(TokenKey(), s_tokens);

    private static string OwnedKey(EAccessorySlot slot) =>
        k_ownedKeyPrefix + s_account + "." + slot;

    private static string TokenKey() => k_tokenKeyPrefix + s_account;
}

[Serializable]
public class CosmeticSlotOwnership
{
    public int Slot;

    public int[] Bits;
}
