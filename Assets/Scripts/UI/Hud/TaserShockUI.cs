using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 테이저 기절 동안 화면을 시안으로 빠르게 지직거리게 하는 감전 연출. App.UI.TaserShock으로 접근한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TaserShockUI : CommonManagerBase
{
    [Tooltip("화면을 덮는 시안 전기 오버레이 이미지. 알파를 코드가 구동한다")]
    [SerializeField] private Image m_shockImage;

    [Tooltip("가장 강할 때의 알파 — 기절 중에도 주변은 보여야 하므로 낮게 잡는다")]
    [SerializeField] private float m_maxAlpha = 0.45f;

    [Tooltip("플리커의 약한 쪽 알파 배수 — 0이면 완전히 껐다 켜져 너무 거칠다")]
    [SerializeField] private float m_flickerLowScale = 0.35f;

    private const float k_flickerMinSeconds = 0.02f;
    private const float k_flickerMaxSeconds = 0.055f;
    private const float k_jitterPixels = 9f;

    private float m_intensity;
    private float m_nextFlickerTime;
    private bool m_flickerHigh = true;
    private Vector2 m_basePosition;

    protected override void Awake()
    {
        base.Awake();

        if (m_shockImage != null)
            m_basePosition = m_shockImage.rectTransform.anchoredPosition;

        OverlayImage.SetAlpha(m_shockImage, 0f);
    }

    /// <summary>감전 강도 갱신 — 오너가 매 프레임 알린다. 0이면 꺼진다.</summary>
    public void SetShock(float intensity)
    {
        m_intensity = Mathf.Clamp01(intensity);
    }

    private void Update()
    {
        if (m_shockImage == null)
            return;

        if (m_intensity <= 0.001f)
        {
            if (m_shockImage.gameObject.activeSelf)
            {
                m_shockImage.rectTransform.anchoredPosition = m_basePosition;
                OverlayImage.SetAlpha(m_shockImage, 0f);
            }
            return;
        }

        if (Time.time >= m_nextFlickerTime)
        {
            m_flickerHigh = !m_flickerHigh;
            m_nextFlickerTime = Time.time + Random.Range(k_flickerMinSeconds, k_flickerMaxSeconds);
            m_shockImage.rectTransform.anchoredPosition = m_basePosition
                + new Vector2(Random.Range(-k_jitterPixels, k_jitterPixels) * m_intensity, 0f);
        }

        float alpha = m_maxAlpha * m_intensity * (m_flickerHigh ? 1f : m_flickerLowScale);
        OverlayImage.SetAlpha(m_shockImage, alpha);
    }
}
