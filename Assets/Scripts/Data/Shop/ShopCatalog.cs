using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 상점 판매 후보 목록 SO. ShopLineup이 매 라운드 일부를 뽑아 주문창 칸에 배정하며, 인덱스가 네트워크 계약이다.
/// </summary>
[CreateAssetMenu(fileName = "ShopCatalog", menuName = "Scriptable Objects/Shop Catalog")]
public class ShopCatalog : ScriptableObject
{
    private const string k_itemTable = "ItemTable";
    private const string k_nameKeyPrefix = "Item.Name.";

    [Serializable]
    public class Entry
    {
        [Tooltip("소지형 판매 품목. 가격·이름·설명은 여기서 읽는다. (비우면 설치형)")]
        [SerializeField]
        private ItemBase m_itemPrefab;

        [Tooltip("설치형 판매 품목. (None이면 소지형)")]
        [SerializeField]
        private EInstallable m_installable = EInstallable.None;

        [Tooltip("설치형 아이템의 가격. (소지형은 ItemBase.ShopPrice 사용)")]
        [Min(0)]
        [SerializeField]
        private int m_installablePrice;

        [Tooltip("고정 등장 그룹. ShopLineup의 고정 칸이 이 표시가 붙은 항목에서만 뽑힌다.")]
        [SerializeField]
        private bool m_staple;

        [Tooltip("소모품(쓰면 없어진다). 본부 재고 게시판이 이 표시가 붙은 항목의 남은 개수를 센다.")]
        [SerializeField]
        private bool m_consumable;

        [Tooltip("설치형 아이콘을 구울 때 쓸 모델 (ItemIconBaker). 소지형은 ItemBase.HeldModelPrefab로 대신한다")]
        [SerializeField]
        private GameObject m_displayModel;

        [Tooltip("아이콘을 구울 때 모델에 씌울 배율 — 눌러 쓰는 설치형은 이 비례가 곧 실물이다")]
        [SerializeField]
        private Vector3 m_displayScale = Vector3.one;

        [Tooltip("주문창 목록에 쓸 아이콘. 비우면 소지형은 ItemBase.ItemIcon으로 대신한다. (설치형은 필수)")]
        [SerializeField]
        private Sprite m_displayIcon;

        public ItemBase ItemPrefab => m_itemPrefab;
        public EInstallable Installable => m_installable;
        public bool IsStaple => m_staple;
        public bool IsConsumable => m_consumable;
        public Vector3 DisplayScale => m_displayScale;

        public bool IsInstallable => m_installable != EInstallable.None;

        public int Price => IsInstallable ? m_installablePrice : (m_itemPrefab != null ? m_itemPrefab.ShopPrice : 0);

        public Sprite Icon =>
            m_displayIcon != null ? m_displayIcon
            : !IsInstallable && m_itemPrefab != null ? m_itemPrefab.ItemIcon
            : null;

        public string DisplayName =>
            IsInstallable ? LocalizedStrings.Get(k_itemTable, k_nameKeyPrefix + m_installable)
            : m_itemPrefab != null && !m_itemPrefab.ItemName.IsEmpty
                ? m_itemPrefab.ItemName.GetLocalizedString()
            : string.Empty;

        public GameObject DisplayModel =>
            m_displayModel != null ? m_displayModel
            : !IsInstallable && m_itemPrefab != null ? m_itemPrefab.HeldModelPrefab
            : null;

        public bool IsValid => IsInstallable ? m_installablePrice > 0 : m_itemPrefab != null;
    }

    [Tooltip("인덱스가 네트워크 계약 — ShopLineup의 칸이 그대로 복제한다")]
    [SerializeField]
    private Entry[] m_entries;

    [Tooltip("고정 등장(Staple) 항목이 채우는 칸 수")]
    [Min(0)]
    [SerializeField]
    private int m_stapleSlots = 5;

    [Tooltip("그 외 항목에서 랜덤으로 채우는 칸 수")]
    [Min(0)]
    [SerializeField]
    private int m_randomSlots = 2;

    public IReadOnlyList<Entry> Entries => m_entries;
    public int Count => m_entries?.Length ?? 0;
    public int StapleSlots => m_stapleSlots;
    public int RandomSlots => m_randomSlots;

    public bool IsValidIndex(int index) => index >= 0 && index < Count && m_entries[index] != null;

    /// <summary>인덱스로 항목을 얻는다 — 범위 밖이면 null.</summary>
    public Entry Get(int index) => IsValidIndex(index) ? m_entries[index] : null;

    /// <summary>이 소지형 프리팹이 몇 번 항목인가 — 없으면 -1. 구매 집계의 역인덱스다.</summary>
    public int IndexOf(ItemBase itemPrefab)
    {
        if (itemPrefab == null || m_entries == null)
            return -1;

        for (int i = 0; i < m_entries.Length; i++)
        {
            Entry entry = m_entries[i];
            if (entry != null && !entry.IsInstallable && entry.ItemPrefab == itemPrefab)
                return i;
        }

        return -1;
    }

    /// <summary>이 설치형이 몇 번 항목인가 — 없으면 -1.</summary>
    public int IndexOf(EInstallable installable)
    {
        if (installable == EInstallable.None || m_entries == null)
            return -1;

        for (int i = 0; i < m_entries.Length; i++)
        {
            Entry entry = m_entries[i];
            if (entry != null && entry.Installable == installable)
                return i;
        }

        return -1;
    }
}
