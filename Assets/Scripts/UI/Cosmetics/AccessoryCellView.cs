using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 치장 선택 한 칸 — 아이콘(없으면 이름)을 보여 주고 눌린 사실만 알린다.
/// </summary>
[RequireComponent(typeof(Button))]
public class AccessoryCellView : MonoBehaviour
{
    [Tooltip("착용 모습 아이콘")]
    [SerializeField] private Image m_icon;

    [Tooltip("아이템 이름")]
    [SerializeField] private TMP_Text m_label;

    [Tooltip("지금 고른 칸에만 켜지는 표시. 없으면 선택 표시가 없다")]
    [SerializeField] private GameObject m_selectedMark;

    [Tooltip("아직 못 얻은 칸에 켜지는 자물쇠. 없으면 흐려지기만 한다")]
    [SerializeField] private GameObject m_lockMark;

    [Tooltip("잠긴 칸의 아이콘 투명도")]
    [SerializeField] private float m_lockedAlpha = 0.3f;

    [Tooltip("다른 치장에 가려진 칸의 아이콘 투명도 — 잠김보다 옅게 흐리지 않는다 (#932)")]
    [SerializeField] private float m_hiddenAlpha = 0.55f;

    private Button m_button;

    private bool m_locked;
    private bool m_hidden;

    private void Awake() => m_button = GetComponent<Button>();

    /// <summary>아이콘·이름·클릭 동작을 채운다 — 찍어 낼 때 한 번 부른다.</summary>
    public void Bind(Sprite icon, string label, Action onPicked)
    {
        if (m_button == null)
            m_button = GetComponent<Button>();

        if (m_icon != null)
        {
            m_icon.sprite = icon;
            m_icon.enabled = icon != null;
        }

        if (m_label != null)
            m_label.text = label;

        m_button.onClick.RemoveAllListeners();
        m_button.onClick.AddListener(() => onPicked?.Invoke());
    }

    /// <summary>이름만 갈아 끼운다 — 언어가 바뀌었을 때.</summary>
    public void SetLabel(string label)
    {
        if (m_label != null)
            m_label.text = label;
    }

    public void SetSelected(bool selected)
    {
        if (m_selectedMark != null)
            m_selectedMark.SetActive(selected);
    }

    /// <summary>미보유 항목을 흐리게 잠긴 칸으로 표시하고 버튼을 끈다.</summary>
    public void SetLocked(bool locked)
    {
        m_locked = locked;
        Apply();
    }

    /// <summary>다른 치장에 가려지는 칸을 흐리게 표시한다(선택은 가능).</summary>
    public void SetHidden(bool hidden)
    {
        m_hidden = hidden;
        Apply();
    }

    private void Apply()
    {
        if (m_lockMark != null)
            m_lockMark.SetActive(m_locked);

        if (m_icon != null)
        {
            Color color = m_icon.color;
            color.a = m_locked ? m_lockedAlpha : (m_hidden ? m_hiddenAlpha : 1f);
            m_icon.color = color;
        }

        if (m_button == null)
            m_button = GetComponent<Button>();

        m_button.interactable = !m_locked;
    }
}
