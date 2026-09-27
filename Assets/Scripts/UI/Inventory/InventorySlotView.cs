using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 인벤토리 핫바의 슬롯 한 칸 — 아이콘·선택 하이라이트와 편집 모드의 호버 툴팁·드래그 이벤트를 담당한다.
/// 로직은 InventoryBarView가 소유한다.
/// </summary>
public class InventorySlotView : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
{
    [Header("표시")]
    [SerializeField] private Image m_background;
    [SerializeField] private Image m_icon;
    [SerializeField] private TextMeshProUGUI m_nameText;

    [Header("하이라이트 색")]
    [SerializeField] private Color m_normalColor = new Color(0f, 0f, 0f, 0.5f);
    [Tooltip("공용 색 팔레트 — 선택 슬롯에 Highlight를 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;
    [Tooltip("선택 슬롯 채움 투명도")]
    [Range(0f, 1f)]
    [SerializeField] private float m_selectedAlpha = 0.7f;

    private InventoryBarView m_owner;
    private int m_index;
    private ItemBase m_item;

    private LocalizedString m_boundName;

    private Transform m_iconOriginalParent;
    private Vector3 m_iconOriginalLocalPosition;

    public ItemBase Item => m_item;

    public int Index => m_index;

    /// <summary>바가 스폰 시 1회 호출 — 슬롯 인덱스와 소유 바를 연결한다.</summary>
    public void Setup(int index, InventoryBarView owner)
    {
        m_index = index;
        m_owner = owner;
    }

    /// <summary>슬롯 내용 갱신. null = 빈 칸 (프레임만 표시).</summary>
    public void Bind(ItemBase item)
    {
        if (m_boundName != null)
        {
            m_boundName.StringChanged -= HandleItemNameChanged;
            m_boundName = null;
        }

        m_item = item;

        if (item == null)
        {
            m_icon.enabled = false;
            m_nameText.text = string.Empty;
            return;
        }

        m_icon.sprite = item.ItemIcon;
        m_icon.enabled = item.ItemIcon != null;

        if (item.ItemIcon != null)
        {
            m_nameText.text = string.Empty;
            return;
        }

        m_boundName = item.ItemName;
        m_boundName.StringChanged += HandleItemNameChanged;
    }

    private void HandleItemNameChanged(string localizedName)
    {
        m_nameText.text = localizedName;
    }

    private void OnDestroy()
    {
        if (m_boundName != null)
        {
            m_boundName.StringChanged -= HandleItemNameChanged;
        }
    }

    /// <summary>선택(장착) 하이라이트 — 배경색 스왑.</summary>
    public void SetSelected(bool selected)
    {
        m_background.color = selected ? SelectedColor : m_normalColor;
    }

    public void OnPointerEnter(PointerEventData eventData) => m_owner.ShowTooltip(this);

    public void OnPointerExit(PointerEventData eventData) => m_owner.HideTooltip();

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!m_owner.IsEditMode || m_item == null)
        {
            eventData.pointerDrag = null;
            return;
        }

        m_iconOriginalParent = m_icon.transform.parent;
        m_iconOriginalLocalPosition = m_icon.transform.localPosition;
        m_icon.transform.SetParent(m_icon.canvas.rootCanvas.transform, true);
        m_icon.raycastTarget = false;
    }

    public void OnDrag(PointerEventData eventData)
    {
        m_icon.transform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        m_icon.transform.SetParent(m_iconOriginalParent, false);
        m_icon.transform.localPosition = m_iconOriginalLocalPosition;
        m_icon.raycastTarget = false;
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (!m_owner.IsEditMode || eventData.pointerDrag == null)
        {
            return;
        }

        InventorySlotView source = eventData.pointerDrag.GetComponent<InventorySlotView>();
        if (source != null && source != this)
        {
            m_owner.RequestSwap(source.Index, m_index);
        }
    }

    private Color SelectedColor
    {
        get
        {
            if (m_palette != null)
                return UiColorPalette.WithAlpha(m_palette.Highlight, m_selectedAlpha);

            Debug.LogWarning("InventorySlotView: 색 팔레트가 연결되지 않았다", this);
            return UiColorPalette.WithAlpha(Color.white, m_selectedAlpha);
        }
    }
}
