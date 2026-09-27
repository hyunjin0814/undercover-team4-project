using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 약탈 창의 소지품 한 칸 — 아이템을 표시하고 클릭하면 가져가기를 요청한다.
/// </summary>
public class LootSlotView : MonoBehaviour, IPointerClickHandler
{
    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TextMeshProUGUI m_nameText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0f, 0f, 0f, 0.5f);

    [SerializeField]
    private Color m_emptyColor = new Color(0f, 0f, 0f, 0.2f);

    private LootPanel m_owner;
    private ItemBase m_item;

    private LocalizedString m_boundName;

    public ItemBase Item => m_item;

    /// <summary>창이 1회 호출 — 소유 창을 연결한다.</summary>
    public void Setup(LootPanel owner) => m_owner = owner;

    /// <summary>칸 내용 갱신. null = 빈 칸.</summary>
    public void Bind(ItemBase item)
    {
        if (m_boundName != null)
        {
            m_boundName.StringChanged -= HandleItemNameChanged;
            m_boundName = null;
        }

        m_item = item;

        if (m_background != null)
            m_background.color = item != null ? m_filledColor : m_emptyColor;

        if (item == null)
        {
            if (m_icon != null)
                m_icon.enabled = false;
            if (m_nameText != null)
                m_nameText.text = string.Empty;
            return;
        }

        if (m_icon != null)
        {
            m_icon.sprite = item.ItemIcon;
            m_icon.enabled = item.ItemIcon != null;
        }

        m_boundName = item.ItemName;
        m_boundName.StringChanged += HandleItemNameChanged;
    }

    private void HandleItemNameChanged(string localizedName)
    {
        if (m_nameText != null)
            m_nameText.text = localizedName;
    }

    private void OnDestroy()
    {
        if (m_boundName != null)
            m_boundName.StringChanged -= HandleItemNameChanged;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (m_item == null || m_owner == null)
            return;

        m_owner.RequestTake(m_item);
    }
}
