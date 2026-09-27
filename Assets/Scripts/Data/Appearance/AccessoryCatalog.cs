using System;
using UnityEngine;

/// <summary>
/// 슬롯별 치장 아이템 목록 SO — 배열 인덱스가 네트워크로 오가는 값이며, 0번은 "안 씀"이다.
/// </summary>
[CreateAssetMenu(fileName = "AccessoryCatalog", menuName = "Undercover/Player/Accessory Catalog")]
public class AccessoryCatalog : ScriptableObject
{
    [Serializable]
    private class Item
    {
        [Tooltip("붙일 프리팹 — 0번은 '안 씀'이라 비워 둘 것. 콜라이더가 있는 프리팹을 넣지 말 것")]
        public GameObject Prefab;

        [Tooltip("선택 칸에 띄울 아이콘 — 비어 있으면 이름만 보인다")]
        public Sprite Icon;

        [Tooltip("이걸 쓰면 가려지는 슬롯 — 전면 헬멧이 머리카락을, 마스크가 수염을 덮는 식")]
        public EAccessorySlotMask Hides;

        [Tooltip("계정을 새로 만들어도 처음부터 쓸 수 있는가 — 나머지는 자판기로 해금한다")]
        public bool DefaultOwned;
    }

    [Serializable]
    private class SlotEntry
    {
        public EAccessorySlot Slot;
        public Item[] Items;
    }

    [SerializeField] private SlotEntry[] m_slots = new SlotEntry[0];

    /// <summary>그 슬롯의 항목 수 — 0번("안 씀")을 포함한 길이다. 목록이 없으면 1(안 씀만).</summary>
    public int CountOf(EAccessorySlot slot)
    {
        SlotEntry entry = Find(slot);
        return entry == null || entry.Items == null ? 1 : Mathf.Max(1, entry.Items.Length);
    }

    /// <summary>붙일 프리팹 — 0이거나 범위 밖이면 null("안 씀")이다.</summary>
    public GameObject Get(EAccessorySlot slot, int index)
    {
        Item item = ItemAt(slot, index);
        return item == null ? null : item.Prefab;
    }

    /// <summary>선택 칸 아이콘 — 없으면 null이고, 그 자리는 이름만 보인다.</summary>
    public Sprite IconOf(EAccessorySlot slot, int index)
    {
        Item item = ItemAt(slot, index);
        return item == null ? null : item.Icon;
    }

    /// <summary>처음부터 쓸 수 있는 기본 지급 항목인지 판정한다. "안 씀"(0)은 항상 참이다.</summary>
    public bool IsDefaultOwned(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return true;

        Item item = ItemAt(slot, index);
        return item != null && item.DefaultOwned;
    }

    /// <summary>이 조합에서 다른 액세서리에 가려지는 슬롯을 돌려준다.</summary>
    public EAccessorySlotMask HiddenSlots(AccessorySet set)
    {
        EAccessorySlotMask hidden = EAccessorySlotMask.None;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            if (IsHidden(hidden, slot))
                continue;

            Item item = ItemAt(slot, set[slot]);
            if (item == null)
                continue;

            hidden |= item.Hides & ~MaskOf(slot);
        }

        return hidden;
    }

    /// <summary>그 슬롯이 <see cref="HiddenSlots"/>에 걸렸는가.</summary>
    public static bool IsHidden(EAccessorySlotMask hidden, EAccessorySlot slot) =>
        (hidden & MaskOf(slot)) != 0;

    public static EAccessorySlotMask MaskOf(EAccessorySlot slot) => (EAccessorySlotMask)(1 << (int)slot);

    private Item ItemAt(EAccessorySlot slot, int index)
    {
        if (index <= 0)
            return null;

        SlotEntry entry = Find(slot);
        if (entry == null || entry.Items == null || index >= entry.Items.Length)
            return null;

        Item item = entry.Items[index];
        return item == null || item.Prefab == null ? null : item;
    }

    private SlotEntry Find(EAccessorySlot slot)
    {
        for (int i = 0; i < m_slots.Length; i++)
            if (m_slots[i] != null && m_slots[i].Slot == slot)
                return m_slots[i];

        return null;
    }
}
