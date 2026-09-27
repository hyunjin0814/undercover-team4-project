using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 약탈 창의 개인 자금 칸 — 대상 잔액(스냅숏)을 보여 주고 누르면 전액 가져가기를 요청한다.
/// </summary>
public class LootFundsView : MonoBehaviour, IPointerClickHandler
{
    [Header("표시")]
    [SerializeField]
    private Image m_background;

    [Tooltip("칸 문구 — 예: HudTable/Hud.Loot.Funds. 비우면 금액만 그린다")]
    [SerializeField]
    private LocalizedString m_label;

    [SerializeField]
    private TextMeshProUGUI m_amountText;

    [Header("색")]
    [SerializeField]
    private Color m_filledColor = new Color(0f, 0f, 0f, 0.5f);

    [SerializeField]
    private Color m_emptyColor = new Color(0f, 0f, 0f, 0.2f);

    private LootPanel m_owner;
    private int m_amount;

    private string m_localizedLabel = string.Empty;

    public int Amount => m_amount;

    /// <summary>창이 1회 호출 — 소유 창을 연결한다.</summary>
    public void Setup(LootPanel owner) => m_owner = owner;

    private void Awake()
    {
        if (!m_label.IsEmpty)
            m_label.StringChanged += HandleLabelChanged;
    }

    private void OnDestroy()
    {
        if (!m_label.IsEmpty)
            m_label.StringChanged -= HandleLabelChanged;
    }

    private void HandleLabelChanged(string localizedLabel)
    {
        m_localizedLabel = localizedLabel;
        Redraw();
    }

    /// <summary>칸 내용 갱신. 0 = 빈 지갑(눌러도 아무 일도 일어나지 않는다).</summary>
    public void Bind(int amount)
    {
        m_amount = amount;
        Redraw();
    }

    private void Redraw()
    {
        if (m_background != null)
            m_background.color = m_amount > 0 ? m_filledColor : m_emptyColor;

        if (m_amountText == null)
            return;

        m_amountText.text = string.IsNullOrEmpty(m_localizedLabel)
            ? m_amount.ToString()
            : $"{m_localizedLabel} {m_amount}";
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (m_amount <= 0 || m_owner == null)
            return;

        m_owner.RequestTakeFunds();
    }
}
