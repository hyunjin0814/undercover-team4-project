using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 한 슬롯의 치장 선택 칸들을 만들고, 고르면 착용 정보에 저장한다. 미보유 항목은 잠긴 칸으로 남긴다.
/// </summary>
public class AccessoryPickerView : MonoBehaviour
{
    [Tooltip("이 줄이 고르는 슬롯")]
    [SerializeField] private EAccessorySlot m_slot;

    [Tooltip("치장 카탈로그 — Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸을 넣을 부모 (Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [SerializeField] private AccessoryCellView m_cellPrefab;

    private readonly List<AccessoryCellView> m_cells = new List<AccessoryCellView>();

    private void Awake()
    {
        if (m_catalog == null || m_container == null || m_cellPrefab == null)
        {
            Debug.LogWarning($"[{nameof(AccessoryPickerView)}] 배선이 빠졌습니다 (#818)", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void OnEnable()
    {
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
        CosmeticNames.OnLanguageChanged += RefreshLabels;
        CosmeticInventory.OnOwnedChanged += RefreshLocks;

        CosmeticInventory.SanitizeEquipped(m_catalog);

        RefreshLocks();
        RefreshSelection();
        RefreshHidden();
    }

    private void OnDisable()
    {
        CosmeticLoadout.OnAccessoryChanged -= HandleAccessoryChanged;
        CosmeticNames.OnLanguageChanged -= RefreshLabels;
        CosmeticInventory.OnOwnedChanged -= RefreshLocks;
    }

    private void Build()
    {
        int count = m_catalog.CountOf(m_slot);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            AccessoryCellView cell = Instantiate(m_cellPrefab, m_container);
            cell.name = $"Accessory {index}";
            cell.Bind(
                m_catalog.IconOf(m_slot, index),
                CosmeticNames.Of(m_catalog.Get(m_slot, index)),
                () => CosmeticLoadout.SetAccessory(m_slot, index)
            );
            m_cells.Add(cell);
        }

        RefreshLocks();
        RefreshSelection();
        RefreshHidden();
    }

    private void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (slot == m_slot)
            RefreshSelection();

        RefreshHidden();
    }

    private void RefreshLabels()
    {
        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetLabel(CosmeticNames.Of(m_catalog.Get(m_slot, i)));
    }

    private void RefreshLocks()
    {
        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetLocked(!CosmeticInventory.IsOwned(m_catalog, m_slot, i));
    }

    private void RefreshSelection()
    {
        int selected = CosmeticLoadout.GetAccessory(m_slot);

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetSelected(i == selected);
    }

    private void RefreshHidden()
    {
        bool hidden = AccessoryCatalog.IsHidden(
            m_catalog.HiddenSlots(AccessorySet.FromSettings()),
            m_slot
        );

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].SetHidden(hidden);
    }
}
