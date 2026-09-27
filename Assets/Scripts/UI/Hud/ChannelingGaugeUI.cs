using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 채널링(체포·구조·스캔) 진행을 표시하는 화면 중앙 원형 게이지. App.UI.Gauge로 접근한다.
/// 서버는 시작·종료만 알리고 진행률은 로컬 시간으로 채운다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ChannelingGaugeUI : CommonManagerBase
{
    [Tooltip("원형 필 이미지 — Image Type: Filled / Radial 360")]
    [SerializeField]
    private Image m_fillImage;

    private float m_duration;
    private float m_elapsed;
    private bool m_isRunning;

    private object m_owner;

    protected override void Awake()
    {
        base.Awake();
        SetVisible(false);
    }

    /// <summary>채널링 시작 — seconds 동안 0→100%로 차오른다. 서버 시작 통지 수신 시 호출.</summary>
    public void Show(float seconds, object owner) => Show(seconds, 0f, owner);

    /// <summary>이미 elapsed초 지난 상태부터 게이지를 이어 표시한다.</summary>
    public void Show(float seconds, float elapsed, object owner)
    {
        if (seconds <= 0f)
            return;

        m_owner = owner;
        m_duration = seconds;
        m_elapsed = Mathf.Clamp(elapsed, 0f, seconds);
        m_isRunning = true;
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);
        SetVisible(true);
    }

    /// <summary>owner가 띄운 게이지일 때만 숨긴다.</summary>
    public void Hide(object owner)
    {
        if (m_owner != null && owner != m_owner)
            return;

        ForceHide();
    }

    private void ForceHide()
    {
        m_isRunning = false;
        m_owner = null;
        SetVisible(false);
    }

    private void Update()
    {
        if (!m_isRunning)
            return;

        m_elapsed += Time.deltaTime;
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);

        if (m_elapsed >= m_duration + 0.5f)
            ForceHide();
    }

    private void SetVisible(bool visible)
    {
        if (m_fillImage != null)
            m_fillImage.gameObject.SetActive(visible);
    }
}
