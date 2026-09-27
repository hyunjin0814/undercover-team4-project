using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 설정 패널의 실시간 미리보기 — 실제 HUD와 같은 CrosshairRenderer로 그린다.
/// </summary>
public class CrosshairPreviewView : MonoBehaviour
{
    [SerializeField]
    private RectTransform m_lineUp;

    [SerializeField]
    private RectTransform m_lineDown;

    [SerializeField]
    private RectTransform m_lineLeft;

    [SerializeField]
    private RectTransform m_lineRight;

    [SerializeField]
    private RectTransform m_dot;

    [SerializeField]
    private RectTransform m_circleRing;

    [SerializeField]
    private Image m_circleImage;

    [SerializeField]
    private PlayerColorPalette m_colorPalette;

    private CrosshairVisualRefs VisualRefs =>
        new CrosshairVisualRefs
        {
            Up = m_lineUp,
            Down = m_lineDown,
            Left = m_lineLeft,
            Right = m_lineRight,
            Dot = m_dot,
            CircleRing = m_circleRing,
            CircleImage = m_circleImage,
        };

    /// <summary>패널이 열려 있는 동안 슬라이더·모양·색이 바뀔 때마다 부른다.</summary>
    public void Refresh(CrosshairSettings settings)
    {
        CrosshairRenderer.ApplyShape(VisualRefs, settings);
        CrosshairRenderer.ApplyColor(VisualRefs, settings, null, m_colorPalette);
    }
}
