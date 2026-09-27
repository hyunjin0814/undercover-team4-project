using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 감정표현 휠의 칸 하나 — 아이콘·이름·강조를 표시한다.
/// </summary>
public class EmoteWheelSlotView : MonoBehaviour
{
    [SerializeField]
    private RectTransform m_root;

    [SerializeField]
    private Image m_icon;

    [SerializeField]
    private TextMeshProUGUI m_label;

    [Tooltip("가리키는 동안 켜지는 강조 표시")]
    [SerializeField]
    private GameObject m_highlight;

    [Tooltip("빈 칸일 때의 아이콘 투명도")]
    [SerializeField]
    private float m_emptyAlpha = 0.25f;

    private UnityEngine.Localization.LocalizedString m_boundName;

    private void Awake()
    {
        if (m_root == null)
            m_root = transform as RectTransform;
    }

    public void SetAnchoredPosition(Vector2 position)
    {
        if (m_root == null)
            m_root = transform as RectTransform;

        if (m_root != null)
            m_root.anchoredPosition = position;
    }

    /// <summary>칸에 감정표현을 채운다 — null이면 빈 칸으로 그린다.</summary>
    public void Bind(EmoteDefinition definition)
    {
        bool filled = definition != null;

        if (m_icon != null)
        {
            m_icon.sprite = filled ? definition.Icon : null;
            m_icon.enabled = filled && definition.Icon != null;

            Color color = m_icon.color;
            color.a = filled ? 1f : m_emptyAlpha;
            m_icon.color = color;
        }

        UnsubscribeLabel();

        if (!filled)
        {
            m_boundName = null;
            SetLabelText(string.Empty);
            return;
        }

        if (definition.DisplayName == null || definition.DisplayName.IsEmpty)
        {
            m_boundName = null;
            SetLabelText(definition.Id);
            return;
        }

        m_boundName = definition.DisplayName;
        m_boundName.StringChanged += SetLabelText;
    }

    private void OnDestroy() => UnsubscribeLabel();

    private void UnsubscribeLabel()
    {
        if (m_boundName == null)
            return;

        m_boundName.StringChanged -= SetLabelText;
        m_boundName = null;
    }

    public void SetHighlighted(bool highlighted)
    {
        if (m_highlight != null)
            m_highlight.SetActive(highlighted);
    }

    private void SetLabelText(string value)
    {
        if (m_label != null)
            m_label.text = value;
    }
}
