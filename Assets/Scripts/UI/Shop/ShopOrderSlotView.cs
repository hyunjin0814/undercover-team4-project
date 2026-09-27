using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 주문창 그리드의 한 칸 — 품목을 표시하고 주문 버튼 클릭을 창에 전달한다.
/// </summary>
public class ShopOrderSlotView : MonoBehaviour
{
    private const string k_itemTable = "ItemTable";
    private const string k_shopTable = "ShopTable";
    private const string k_commonTable = "CommonTable";
    private const string k_descriptionKeyPrefix = "Item.Description.";
    private const string k_moneyKey = "Common.Unit.Money";

    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_priceText;

    [Tooltip("설명 줄. 비워 두면 설명을 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_descriptionText;

    [Header("주문")]
    [SerializeField]
    private Button m_orderButton;

    [SerializeField]
    private TMP_Text m_orderButtonText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0.031f, 0.078f, 0.122f, 0.93f);

    [SerializeField]
    private Color m_emptyColor = new Color(0.031f, 0.078f, 0.122f, 0.45f);

    [Tooltip("품절일 때 주문 버튼 글자색")]
    [SerializeField]
    private Color m_soldOutColor = new Color(1f, 0.361f, 0.416f, 1f);

    [Tooltip("이미 산 설치형일 때 주문 버튼 글자색")]
    [SerializeField]
    private Color m_ownedColor = new Color(0.271f, 0.890f, 0.604f, 1f);

    private ShopBrowserPanel m_owner;
    private int m_slot = -1;

    private Color m_orderLabelColor = Color.white;
    private bool m_orderLabelColorCached;

    /// <summary>창이 1회 호출 — 소유 창을 연결하고 버튼을 건다.</summary>
    public void Setup(ShopBrowserPanel owner)
    {
        m_owner = owner;

        if (m_orderButtonText != null && !m_orderLabelColorCached)
        {
            m_orderLabelColor = m_orderButtonText.color;
            m_orderLabelColorCached = true;
        }

        if (m_orderButton != null)
        {
            m_orderButton.onClick.RemoveListener(HandleOrderClicked);
            m_orderButton.onClick.AddListener(HandleOrderClicked);
        }
    }

    /// <summary>칸 내용 갱신. entry가 null이면 빈 칸.</summary>
    public void Bind(int slot, ShopCatalog.Entry entry, EShopSlotStatus status)
    {
        m_slot = entry != null ? slot : -1;

        if (m_background != null)
            m_background.color = entry != null ? m_filledColor : m_emptyColor;

        if (entry == null)
        {
            SetEmpty();
            return;
        }

        if (m_icon != null)
        {
            m_icon.sprite = entry.Icon;
            m_icon.enabled = entry.Icon != null;
        }

        SetText(m_nameText, ResolveName(entry));
        SetText(m_priceText, LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Price));
        SetText(m_descriptionText, ResolveDescription(entry));

        bool orderable = status == EShopSlotStatus.Available;

        SetText(m_orderButtonText, LocalizedStrings.Get(k_shopTable, StatusKey(status)));

        if (m_orderButtonText != null)
        {
            m_orderButtonText.color =
                status == EShopSlotStatus.SoldOut ? m_soldOutColor
                : status == EShopSlotStatus.Owned ? m_ownedColor
                : m_orderLabelColor;
        }

        if (m_orderButton != null)
        {
            m_orderButton.gameObject.SetActive(true);
            m_orderButton.interactable = orderable;
        }
    }

    private static string StatusKey(EShopSlotStatus status) =>
        status == EShopSlotStatus.SoldOut ? "Shop.Order.SoldOut"
        : status == EShopSlotStatus.Owned ? "Shop.Order.Owned"
        : "Shop.Order.Button";

    private void SetEmpty()
    {
        if (m_icon != null)
            m_icon.enabled = false;

        SetText(m_nameText, LocalizedStrings.Get(k_shopTable, "Shop.Order.Empty"));
        SetText(m_priceText, string.Empty);
        SetText(m_descriptionText, string.Empty);

        if (m_orderButton != null)
            m_orderButton.gameObject.SetActive(false);
    }

    private static string ResolveName(ShopCatalog.Entry entry)
    {
        string name = entry.DisplayName;
        return string.IsNullOrEmpty(name) ? LocalizedStrings.Get(k_shopTable, "Shop.Order.Empty") : name;
    }

    private static string ResolveDescription(ShopCatalog.Entry entry)
    {
        if (entry.IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_descriptionKeyPrefix + entry.Installable);

        return entry.ItemPrefab != null && !entry.ItemPrefab.ItemDescription.IsEmpty
            ? entry.ItemPrefab.ItemDescription.GetLocalizedString()
            : string.Empty;
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null)
            label.text = text;
    }

    private void HandleOrderClicked()
    {
        if (m_slot < 0 || m_owner == null)
            return;

        m_owner.RequestOrder(m_slot);
    }

    private void OnDestroy()
    {
        if (m_orderButton != null)
            m_orderButton.onClick.RemoveListener(HandleOrderClicked);
    }
}
