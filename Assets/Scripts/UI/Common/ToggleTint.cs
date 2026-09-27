using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 체크박스 배경색을 켜짐/꺼짐에 따라 바꾼다. SetIsOnWithoutNotify 후에는 Refresh를 호출할 것.
/// </summary>
[RequireComponent(typeof(Toggle))]
public class ToggleTint : MonoBehaviour
{
    [Tooltip("칠할 배경 — 비워 두면 Toggle의 targetGraphic을 쓴다")]
    [SerializeField] private Image m_background;

    [Tooltip("켜졌을 때 배경색")]
    [SerializeField] private Color m_onColor = new Color(0.878f, 0.663f, 0.290f);

    [Tooltip("꺼졌을 때 배경색")]
    [SerializeField] private Color m_offColor = new Color(0.122f, 0.153f, 0.200f);

    private Toggle m_toggle;

    private void Awake()
    {
        m_toggle = GetComponent<Toggle>();

        if (m_background == null)
            m_background = m_toggle.targetGraphic as Image;

        m_toggle.onValueChanged.AddListener(HandleValueChanged);
    }

    private void OnDestroy()
    {
        if (m_toggle != null)
            m_toggle.onValueChanged.RemoveListener(HandleValueChanged);
    }

    private void OnEnable() => Refresh();

    /// <summary>지금 Toggle 상태로 배경색을 맞춘다.</summary>
    public void Refresh()
    {
        if (m_toggle == null || m_background == null)
            return;

        m_background.color = m_toggle.isOn ? m_onColor : m_offColor;
    }

    private void HandleValueChanged(bool on) => Refresh();
}
