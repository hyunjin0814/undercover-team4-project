using UnityEngine;

/// <summary>
/// 문 하나의 본부 미니맵 마커 — 열림/잠김/닫힘은 색조로, 콘솔 선택 여부는 밝기로 표시한다.
/// </summary>
public class DoorMinimapMarker : MonoBehaviour
{
    [Header("대상 (비우면 자동 탐색)")]
    [SerializeField]
    private InteractableDoor m_door;

    [Tooltip("비우면 같은 오브젝트에서 자동 탐색")]
    [SerializeField]
    private MinimapTarget m_marker;

    [Header("상태 색조")]
    [SerializeField]
    private Color m_openColor = new Color(0.2f, 1f, 0.45f);

    [SerializeField]
    private Color m_lockedColor = new Color(1f, 0.3f, 0.3f);

    [SerializeField]
    private Color m_closedColor = new Color(0.55f, 0.6f, 0.7f);

    [Tooltip("선택되지 않은 문의 밝기 배율 — 1이면 선택된 문과 구분되지 않는다")]
    [Range(0.1f, 1f)]
    [SerializeField]
    private float m_unselectedDim = 0.45f;

    private bool m_isSelected;

    private void Awake()
    {
        if (m_door == null)
            m_door = GetComponentInParent<InteractableDoor>();
        if (m_marker == null)
            m_marker = GetComponent<MinimapTarget>();
    }

    private void OnEnable()
    {
        if (m_door != null)
            m_door.OnOpenChanged += HandleOpenChanged;
        Apply();
    }

    private void OnDisable()
    {
        if (m_door != null)
            m_door.OnOpenChanged -= HandleOpenChanged;
    }

    private void HandleOpenChanged(bool open) => Apply();

    /// <summary>이 문이 지금 본부 콘솔에서 선택된 문인지 — 콘솔이 밀어준다.</summary>
    public void SetSelected(bool selected)
    {
        m_isSelected = selected;
        Apply();
    }

    private void Apply()
    {
        if (m_marker == null)
            return;

        Color color = StateColor;
        m_marker.IconColor = m_isSelected
            ? color
            : new Color(
                color.r * m_unselectedDim,
                color.g * m_unselectedDim,
                color.b * m_unselectedDim,
                color.a
            );
    }

    private Color StateColor
    {
        get
        {
            if (m_door == null)
                return m_closedColor;
            if (m_door.IsOpen)
                return m_openColor;
            return m_door.IsLocked ? m_lockedColor : m_closedColor;
        }
    }
}
