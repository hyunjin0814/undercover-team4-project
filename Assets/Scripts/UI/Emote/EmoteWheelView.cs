using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 홀드 중에만 뜨는 8칸 방사형 감정표현 휠. 칸 배치 각도는 EmoteWheelGeometry에서 받는다.
/// </summary>
public class EmoteWheelView : MonoBehaviour
{
    [Tooltip("휠 전체 루트 — 닫을 때 끈다")]
    [SerializeField]
    private GameObject m_root;

    [Tooltip("칸 8개. 인덱스 = 슬롯 번호(0 = 12시, 시계방향)")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteWheelGeometry.k_slotCount];

    [Tooltip("칸 중심이 놓일 반경(px)")]
    [SerializeField]
    private float m_radius = 160f;

    private PlayerEmoteInput m_input;

    private void Awake()
    {
        LayOutSlots();
        Close();
    }

    /// <summary>휠을 띄우고 현재 구성을 채운다. 홀드가 시작될 때 PlayerEmoteInput이 부른다.</summary>
    public void Open(EmoteLoadout slots, EmoteCatalog catalog, PlayerEmoteInput input)
    {
        m_input = input;

        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            EmoteDefinition definition = null;
            if (catalog != null)
            {
                int index = catalog.IndexOf(slots?.GetSlot(slot));
                definition = catalog.Get(index);
            }

            m_slotViews[slot].Bind(definition);
            m_slotViews[slot].SetHighlighted(false);
        }

        if (m_root != null)
            m_root.SetActive(true);
    }

    /// <summary>휠을 내린다.</summary>
    public void Close()
    {
        m_input = null;

        if (m_root != null)
            m_root.SetActive(false);
    }

    private void Update()
    {
        if (m_input == null || m_root == null || !m_root.activeSelf)
            return;

        int highlighted = EmoteWheelGeometry.SlotFromDirection(m_input.WheelDirection);
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] != null)
                m_slotViews[slot].SetHighlighted(slot == highlighted);
        }
    }

    private void LayOutSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            float radians = EmoteWheelGeometry.SlotCenterDegrees(slot) * Mathf.Deg2Rad;
            var position = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * m_radius;
            m_slotViews[slot].SetAnchoredPosition(position);
        }
    }
}
