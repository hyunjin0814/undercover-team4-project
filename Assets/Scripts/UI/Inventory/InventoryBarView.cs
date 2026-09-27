using System;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 하단 5칸 인벤토리 핫바 — 보유 아이템과 장착 슬롯을 표시하고 이름 팝업을 띄운다.
/// I 편집 모드에서는 드래그 정렬·툴팁을 지원한다. 데이터는 PlayerLoadout.Slots가 진실이다.
/// </summary>
public class InventoryBarView : NetworkBehaviour
{
    [Header("플레이어 참조")]
    [SerializeField]
    private PlayerLoadout m_loadout;

    [SerializeField]
    private PlayerItemUser m_itemUser;

    [SerializeField]
    private PlayerInputHandler m_inputHandler;

    [Header("UI 참조")]
    [Tooltip("핫바 패널 루트 — 비오너에선 통째로 꺼진다.")]
    [SerializeField]
    private GameObject m_barRoot;

    [SerializeField]
    private Image m_barBackground;

    [SerializeField]
    private InventorySlotView[] m_slotViews;

    [Tooltip("선택/줍기 시 아이템 이름 팝업 라벨.")]
    [SerializeField]
    private TextMeshProUGUI m_itemNameLabel;

    [Header("툴팁 (편집 모드 호버)")]
    [SerializeField]
    private GameObject m_tooltipPanel;

    [SerializeField]
    private TextMeshProUGUI m_tooltipName;

    [SerializeField]
    private TextMeshProUGUI m_tooltipDescription;

    [Tooltip("호버한 슬롯 위로 띄울 세로 오프셋(px).")]
    [SerializeField]
    private float m_tooltipOffsetY = 110f;

    [Header("연출")]
    [SerializeField]
    private float m_itemNameDuration = 1.5f;

    [SerializeField]
    private Color m_barNormalColor = new Color(0f, 0f, 0f, 0.25f);

    [SerializeField]
    private Color m_barEditColor = new Color(0.2f, 0.5f, 1f, 0.35f);

    private bool m_isEditMode;
    private int m_itemNameVersion;
    private readonly ItemBase[] m_lastSlots = new ItemBase[PlayerLoadout.k_maxHeldItems];

    public bool IsEditMode => m_isEditMode;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            m_barRoot.SetActive(false);
            enabled = false;
            return;
        }

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_slotViews[i].Setup(i, this);
        }

        m_loadout.OnSlotsChanged += HandleSlotsChanged;
        m_loadout.OnEquippedSlotChanged += RefreshHighlight;
        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        m_inputHandler.OnToggleInventory += ToggleEditMode;

        m_itemNameLabel.gameObject.SetActive(false);
        m_tooltipPanel.SetActive(false);
        m_barBackground.color = m_barNormalColor;
        HandleSlotsChanged();
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
        {
            return;
        }

        SetEditMode(false);

        m_loadout.OnSlotsChanged -= HandleSlotsChanged;
        m_loadout.OnEquippedSlotChanged -= RefreshHighlight;
        m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
        m_inputHandler.OnToggleInventory -= ToggleEditMode;
    }

    private void Update()
    {
        if (!m_isEditMode)
        {
            return;
        }

        if (m_inputHandler.MoveInput != Vector2.zero)
        {
            SetEditMode(false);
        }
    }

    private void HandleSlotsChanged()
    {
        ItemBase newItem = null;
        int newCount = 0;
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase current = m_loadout.Slots[i];
            if (current != null && Array.IndexOf(m_lastSlots, current) < 0)
            {
                newItem = current;
                newCount++;
            }
        }

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_lastSlots[i] = m_loadout.Slots[i];
            m_slotViews[i].Bind(m_loadout.Slots[i]);
        }

        RefreshHighlight();

        if (newCount == 1)
        {
            ShowItemName(newItem);
        }
    }

    private void HandleEquippedItemChanged(ItemBase item)
    {
        if (item != null)
        {
            ShowItemName(item);
        }
    }

    private void RefreshHighlight()
    {
        int equipped = m_loadout.EquippedIndex;
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_slotViews[i].SetSelected(i == equipped);
        }
    }

    private void ShowItemName(ItemBase item)
    {
        m_itemNameLabel.text = item.ItemName.GetLocalizedString();
        m_itemNameLabel.gameObject.SetActive(true);
        HideItemNameAsync(++m_itemNameVersion).Forget();
    }

    private async UniTaskVoid HideItemNameAsync(int version)
    {
        await UniTask.Delay(TimeSpan.FromSeconds(m_itemNameDuration));

        if (this == null || version != m_itemNameVersion)
        {
            return;
        }

        m_itemNameLabel.gameObject.SetActive(false);
    }

    private void ToggleEditMode()
    {
        if (m_loadout.IsIncapacitated && !m_isEditMode)
        {
            return;
        }

        if (m_loadout.IsTerminalFocused && !m_isEditMode)
        {
            return;
        }

        SetEditMode(!m_isEditMode);
    }

    private void SetEditMode(bool on)
    {
        if (m_isEditMode == on)
        {
            return;
        }

        m_isEditMode = on;

        if (on)
        {
            m_itemUser.CancelUse();
            CursorLock.PushUnlock();
        }
        else
        {
            CursorLock.PopUnlock();
            HideTooltip();
        }

        m_barBackground.color = on ? m_barEditColor : m_barNormalColor;
    }

    /// <summary>슬롯 드래그 정렬 완료 — PlayerLoadout에 스왑을 위임한다. 표시는 OnSlotsChanged로 돌아온다.</summary>
    public void RequestSwap(int from, int to) => m_loadout.SwapSlots(from, to);

    /// <summary>슬롯 호버 진입 — 편집 모드에서 아이템 이름·설명 툴팁을 슬롯 위에 띄운다.</summary>
    public void ShowTooltip(InventorySlotView slot)
    {
        if (!m_isEditMode || slot.Item == null)
        {
            return;
        }

        m_tooltipName.text = slot.Item.ItemName.GetLocalizedString();
        m_tooltipDescription.text = slot.Item.ItemDescription.GetLocalizedString();
        m_tooltipPanel.transform.position =
            slot.transform.position + new Vector3(0f, m_tooltipOffsetY, 0f);
        m_tooltipPanel.SetActive(true);
    }

    public void HideTooltip() => m_tooltipPanel.SetActive(false);
}
