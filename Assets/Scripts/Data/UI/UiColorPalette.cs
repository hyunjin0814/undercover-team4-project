using UnityEngine;

/// <summary>
/// UI 공용 색 팔레트 SO — 성공/실패/중립/주의 등 의미 단위로 색을 정의한다.
/// 알파는 담지 않고 쓰는 쪽이 WithAlpha로 얹는다.
/// </summary>
[CreateAssetMenu(fileName = "UiColors", menuName = "Scriptable Objects/UiColorPalette")]
public class UiColorPalette : ScriptableObject
{
    [Tooltip("성공·달성 — 진범 검거, 목표 금액 달성 등")]
    [SerializeField] private Color m_positive = new Color(0.290f, 0.871f, 0.502f);

    [Tooltip("실패·오류 — 오검거 등")]
    [SerializeField] private Color m_negative = new Color(0.863f, 0.149f, 0.149f);

    [Tooltip("중립 — 성패로 가르지 않는 알림. 경범죄 등")]
    [SerializeField] private Color m_neutral = new Color(0.612f, 0.639f, 0.686f);

    [Tooltip("주의 — 조건 불충족처럼 실패는 아니지만 짚어야 하는 경우")]
    [SerializeField] private Color m_caution = new Color(0.984f, 0.749f, 0.141f);

    [Tooltip("강조 — 상호작용 가능·선택됨. 조준 윤곽선·크로스헤어·슬롯 선택이 공유한다")]
    [SerializeField] private Color m_highlight = new Color(1f, 0.85f, 0.2f);

    public Color Positive => m_positive;
    public Color Negative => m_negative;
    public Color Neutral => m_neutral;
    public Color Caution => m_caution;
    public Color Highlight => m_highlight;

    /// <summary>알파만 갈아 끼운 색을 돌려준다 — 팔레트는 RGB만 정하고 투명도는 쓰는 쪽 몫이다.</summary>
    public static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.r, color.g, color.b, alpha);
    }
}
