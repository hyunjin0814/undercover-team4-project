using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HP 바의 채움과 숫자를 그리는 순수 표현 컴포넌트. 값은 PlayerHpUI 등이 넣어 준다.
/// </summary>
public class HpBarView : MonoBehaviour
{
    [Tooltip("채움 이미지 — Image Type Filled, Horizontal, Origin Left. Sprite가 비면 채움이 동작하지 않는다")]
    [SerializeField]
    private Image m_fill;

    [Tooltip("틀 안에 겹쳐 놓는 현재/최대 수치. 게이지로 바꿔도 숫자는 남긴다 — 정확한 값이 필요한 판단(진압봉 몇 대를 더 버티나)이 있다")]
    [SerializeField]
    private TextMeshProUGUI m_hpText;

    /// <summary>채움과 숫자를 값에 맞춘다.</summary>
    public void SetHealth(int current, int max)
    {
        if (m_fill != null)
            m_fill.fillAmount = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;

        if (m_hpText != null)
            m_hpText.text = current + " / " + max;
    }
}
