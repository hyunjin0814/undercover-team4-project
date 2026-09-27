using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 피격 화면 연출 — 붉은 비네트·피격 방향 아크·저체력 글리치·다운 유예 어두워짐. App.UI.DamageVignette로 접근한다.
/// 방향은 환산된 각도로 받는 순수 표현 컴포넌트다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class DamageVignetteUI : CommonManagerBase
{
    [Header("붉은 비네트")]
    [Tooltip(
        "화면을 덮는 비네트 이미지 — 가장자리만 붉은 텍스처. 알파를 코드가 구동하므로 씬에서는 비활성으로 둬도 된다"
    )]
    [SerializeField]
    private Image m_vignetteImage;

    [Tooltip("피격 후 비네트가 완전히 빠질 때까지의 시간(초)")]
    [SerializeField]
    private float m_vignetteSeconds = 0.4f;

    [Tooltip("가장 강한 피격의 비네트 알파 — 1로 두면 화면이 통째로 붉어져 앞이 안 보인다")]
    [SerializeField]
    private float m_vignetteMaxAlpha = 0.55f;

    [Tooltip("이 데미지 이상이면 비네트가 최대 강도로 나온다. 그 미만은 비례해서 옅어진다")]
    [SerializeField]
    private int m_damageForMaxAlpha = 34;

    [Header("피격 방향 아크")]
    [Tooltip(
        "화면 중앙을 축으로 회전시킬 부채꼴 이미지 — 기본 회전(0도)이 화면 위쪽(정면)을 가리키도록 배치할 것"
    )]
    [SerializeField]
    private RectTransform m_directionArc;

    [Tooltip(
        "방향 아크가 표시되는 시간(초) — 비네트보다 길게. '어디서 맞았나'가 비네트보다 오래 필요하다"
    )]
    [SerializeField]
    private float m_arcSeconds = 0.8f;

    [SerializeField]
    private float m_arcMaxAlpha = 0.85f;

    [Header("다운 유예 어두워짐 (#725)")]
    [Tooltip("다운 유예 잔여에 따라 어두워지는 전체 화면 오버레이 — 알파를 코드가 직접 구동한다")]
    [SerializeField]
    private Image m_downDarknessImage;

    [Tooltip("완전히 어두워졌을 때(유예 만료 직전)의 알파 — 1이면 화면이 통째로 캄캄해진다")]
    [SerializeField]
    private float m_downDarknessMaxAlpha = 0.85f;

    [Tooltip("화면 중앙에 크게 뜨는 다운 유예 잔여 초 — 부가 설명 없이 숫자만 (#725)")]
    [SerializeField]
    private TextMeshProUGUI m_downCountdownText;

    [Tooltip("이 초 이하로 남으면 숫자를 경고색으로 바꾼다")]
    [SerializeField]
    private int m_downCountdownWarningSeconds = 10;

    [Tooltip("경고 구간 숫자 색상")]
    [SerializeField]
    private Color m_downCountdownWarningColor = Color.red;

    private Color m_downCountdownNormalColor = Color.white;

    [Header("저체력 글리치")]
    [Tooltip(
        "화면 가장자리 스캔라인·노이즈 오버레이 — 순간 연출이 아니라 저체력 동안 상시 표시된다"
    )]
    [SerializeField]
    private Image m_glitchImage;

    [Tooltip("이 체력 비율 이하에서 글리치가 켜진다")]
    [SerializeField]
    private float m_lowHealthThreshold = 0.3f;

    [Tooltip("글리치 상시 알파 — 노이즈 버스트가 아닐 때의 기본값")]
    [SerializeField]
    private float m_glitchBaseAlpha = 0.18f;

    [Tooltip("간헐 노이즈 버스트 시의 알파")]
    [SerializeField]
    private float m_glitchBurstAlpha = 0.5f;

    private const float k_glitchBurstMinInterval = 0.5f;
    private const float k_glitchBurstMaxInterval = 2f;
    private const float k_glitchBurstMinSeconds = 0.05f;
    private const float k_glitchBurstMaxSeconds = 0.12f;
    private const float k_glitchJitterPixels = 6f;

    private float m_vignetteElapsed = -1f;
    private float m_vignetteStartAlpha;

    private float m_arcElapsed = -1f;

    private bool m_glitchActive;
    private float m_glitchNextBurstTime;
    private float m_glitchBurstUntil;
    private Vector2 m_glitchBasePosition;

    protected override void Awake()
    {
        base.Awake();

        if (m_glitchImage != null)
            m_glitchBasePosition = m_glitchImage.rectTransform.anchoredPosition;

        if (m_downCountdownText != null)
            m_downCountdownNormalColor = m_downCountdownText.color;

        OverlayImage.SetAlpha(m_vignetteImage, 0f);
        SetArcAlpha(0f);
        OverlayImage.SetAlpha(m_glitchImage, 0f);
        OverlayImage.SetAlpha(m_downDarknessImage, 0f);
        HideDownCountdown();
    }

    /// <summary>다운 유예 잔여 비율을 화면 어두움에 그대로 반영한다.</summary>
    public void SetDownDarkness(float darknessRatio)
    {
        OverlayImage.SetAlpha(
            m_downDarknessImage,
            Mathf.Clamp01(darknessRatio) * m_downDarknessMaxAlpha
        );
    }

    /// <summary>다운 유예 잔여 초를 화면 중앙에 큰 숫자로 띄운다 — 부가 설명 없이 숫자만.</summary>
    public void ShowDownCountdown(int remainingSeconds)
    {
        if (m_downCountdownText == null)
            return;

        m_downCountdownText.text = remainingSeconds.ToString();
        m_downCountdownText.color =
            remainingSeconds <= m_downCountdownWarningSeconds
                ? m_downCountdownWarningColor
                : m_downCountdownNormalColor;
        m_downCountdownText.gameObject.SetActive(true);
    }

    public void HideDownCountdown()
    {
        if (m_downCountdownText != null)
            m_downCountdownText.gameObject.SetActive(false);
    }

    /// <summary>피격 연출 — 비네트만 띄운다. 가해자를 모르는 피해(출처 불명 환경 피해)에 쓴다.</summary>
    public void PlayHit(int damage)
    {
        if (damage <= 0)
            return;

        float alpha =
            Mathf.Clamp01((float)damage / Mathf.Max(1, m_damageForMaxAlpha)) * m_vignetteMaxAlpha;

        m_vignetteStartAlpha = Mathf.Max(alpha, CurrentVignetteAlpha());
        m_vignetteElapsed = 0f;
    }

    /// <summary>피격 연출 — 비네트 + 방향 아크.</summary>
    public void PlayHit(int damage, float directionAngleDegrees)
    {
        PlayHit(damage);

        if (damage <= 0 || m_directionArc == null)
            return;

        m_directionArc.localRotation = Quaternion.Euler(0f, 0f, -directionAngleDegrees);
        m_arcElapsed = 0f;
    }

    /// <summary>체력 비율과 무력화 여부로 저체력 글리치를 켜고 끈다.</summary>
    public void UpdateHealthState(float healthRatio, bool incapacitated)
    {
        bool shouldGlitch = !incapacitated && healthRatio <= m_lowHealthThreshold;
        if (shouldGlitch == m_glitchActive)
            return;

        m_glitchActive = shouldGlitch;
        if (!shouldGlitch)
        {
            m_glitchBurstUntil = 0f;
            OverlayImage.SetAlpha(m_glitchImage, 0f);
            if (m_glitchImage != null)
                m_glitchImage.rectTransform.anchoredPosition = m_glitchBasePosition;
            return;
        }

        m_glitchNextBurstTime =
            Time.time + Random.Range(k_glitchBurstMinInterval, k_glitchBurstMaxInterval);
    }

    /// <summary>연출을 즉시 걷어낸다 — 라운드 사이 리셋·오너 교체 지점에서 부른다.</summary>
    public void ClearAll()
    {
        m_vignetteElapsed = -1f;
        m_arcElapsed = -1f;
        UpdateHealthState(1f, false);
        OverlayImage.SetAlpha(m_vignetteImage, 0f);
        SetArcAlpha(0f);
        OverlayImage.SetAlpha(m_downDarknessImage, 0f);
        HideDownCountdown();
    }

    private void Update()
    {
        TickVignette();
        TickArc();
        TickGlitch();
    }

    private void TickVignette()
    {
        if (m_vignetteElapsed < 0f)
            return;

        m_vignetteElapsed += Time.deltaTime;
        if (m_vignetteElapsed >= m_vignetteSeconds)
        {
            m_vignetteElapsed = -1f;
            OverlayImage.SetAlpha(m_vignetteImage, 0f);
            return;
        }

        OverlayImage.SetAlpha(m_vignetteImage, CurrentVignetteAlpha());
    }

    private float CurrentVignetteAlpha()
    {
        if (m_vignetteElapsed < 0f || m_vignetteSeconds <= 0f)
            return 0f;

        return m_vignetteStartAlpha * (1f - m_vignetteElapsed / m_vignetteSeconds);
    }

    private void TickArc()
    {
        if (m_arcElapsed < 0f)
            return;

        m_arcElapsed += Time.deltaTime;
        if (m_arcElapsed >= m_arcSeconds)
        {
            m_arcElapsed = -1f;
            SetArcAlpha(0f);
            return;
        }

        float remaining = 1f - m_arcElapsed / m_arcSeconds;
        SetArcAlpha(m_arcMaxAlpha * Mathf.Clamp01(remaining / 0.3f));
    }

    private void TickGlitch()
    {
        if (!m_glitchActive || m_glitchImage == null)
            return;

        if (Time.time >= m_glitchNextBurstTime)
        {
            m_glitchBurstUntil =
                Time.time + Random.Range(k_glitchBurstMinSeconds, k_glitchBurstMaxSeconds);
            m_glitchNextBurstTime =
                m_glitchBurstUntil
                + Random.Range(k_glitchBurstMinInterval, k_glitchBurstMaxInterval);

            m_glitchImage.rectTransform.anchoredPosition =
                m_glitchBasePosition
                + new Vector2(Random.Range(-k_glitchJitterPixels, k_glitchJitterPixels), 0f);
        }

        bool bursting = Time.time < m_glitchBurstUntil;
        if (!bursting)
            m_glitchImage.rectTransform.anchoredPosition = m_glitchBasePosition;

        OverlayImage.SetAlpha(m_glitchImage, bursting ? m_glitchBurstAlpha : m_glitchBaseAlpha);
    }

    private void SetArcAlpha(float alpha)
    {
        if (m_directionArc != null && m_directionArc.TryGetComponent(out Image image))
            OverlayImage.SetAlpha(image, alpha);
    }
}
