using System;
using UnityEngine;

/// <summary>
/// 감정표현 휠 8칸에 배치한 감정표현 id를 로컬(PlayerPrefs)에 저장·복원하는 순수 모델.
/// </summary>
public class EmoteLoadout
{
    public const int k_slotCount = EmoteWheelGeometry.k_slotCount;

    private const string k_prefsKeyPrefix = "Emote.Loadout.";

    private const string k_unknownOwner = "local";

    private const char k_separator = '|';

    private readonly string[] m_slots = new string[k_slotCount];

    private readonly string m_prefsKey;

    /// <summary>ownerId(계정 식별자)별로 저장 칸을 나눠 구성을 만든다. 비우면 local 칸을 쓴다.</summary>
    public EmoteLoadout(string ownerId = null)
    {
        m_prefsKey =
            k_prefsKeyPrefix + (string.IsNullOrWhiteSpace(ownerId) ? k_unknownOwner : ownerId);
    }

    /// <summary>칸에 든 감정표현 id — 비었거나 범위 밖이면 null.</summary>
    public string GetSlot(int slot) => slot >= 0 && slot < k_slotCount ? m_slots[slot] : null;

    /// <summary>칸을 채우거나(id) 비운다(null·빈 문자열). 범위 밖이면 아무것도 하지 않는다.</summary>
    public void SetSlot(int slot, string emoteId)
    {
        if (slot < 0 || slot >= k_slotCount)
            return;

        m_slots[slot] = string.IsNullOrEmpty(emoteId) ? null : emoteId;
    }

    /// <summary>PlayerPrefs에 담을 한 줄 문자열로 만든다.</summary>
    public string Serialize()
    {
        var parts = new string[k_slotCount];
        for (int slot = 0; slot < k_slotCount; slot++)
            parts[slot] = m_slots[slot] ?? string.Empty;

        return string.Join(k_separator.ToString(), parts);
    }

    /// <summary>저장 문자열에서 구성을 복원한다. 칸 수가 맞지 않아도 모자라면 비우고 넘치면 버린다.</summary>
    public void Deserialize(string raw)
    {
        Array.Clear(m_slots, 0, k_slotCount);

        if (string.IsNullOrEmpty(raw))
            return;

        string[] parts = raw.Split(k_separator);
        int count = Mathf.Min(parts.Length, k_slotCount);
        for (int slot = 0; slot < count; slot++)
            SetSlot(slot, parts[slot]);
    }

    /// <summary>구성을 PlayerPrefs에 저장한다. flush가 false면 디스크 쓰기를 미룬다.</summary>
    public void Save(bool flush = true)
    {
        PlayerPrefs.SetString(m_prefsKey, Serialize());

        if (flush)
            PlayerPrefs.Save();
    }

    /// <summary>저장된 구성을 읽는다. 저장된 적이 없으면 전 칸이 빈 상태로 남는다.</summary>
    public void Load()
    {
        Deserialize(PlayerPrefs.GetString(m_prefsKey, string.Empty));
    }
}
