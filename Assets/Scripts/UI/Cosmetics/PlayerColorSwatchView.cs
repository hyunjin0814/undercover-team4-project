using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 팔레트의 색 한 칸 — 눌린 사실만 알린다.
/// </summary>
[RequireComponent(typeof(Button))]
public class PlayerColorSwatchView : MonoBehaviour
{
    [Tooltip("색이 칠해질 그림 — 비워 두면 이 오브젝트의 Image를 쓴다")]
    [SerializeField]
    private Image m_fill;

    [Tooltip("지금 고른 칸에만 켜지는 표시(테두리 등). 없으면 선택 표시가 없다")]
    [SerializeField]
    private GameObject m_selectedMark;

    private Button m_button;

    private void Awake()
    {
        m_button = GetComponent<Button>();

        if (m_fill == null)
            m_fill = GetComponent<Image>();
    }

    /// <summary>색과 클릭 동작을 채운다 — 찍어 낼 때 한 번 부른다.</summary>
    public void Bind(Color color, System.Action onPicked)
    {
        if (m_fill != null)
            m_fill.color = color;

        m_button.onClick.RemoveAllListeners();
        m_button.onClick.AddListener(() => onPicked?.Invoke());
    }

    public void SetSelected(bool selected)
    {
        if (m_selectedMark != null)
            m_selectedMark.SetActive(selected);
    }
}
