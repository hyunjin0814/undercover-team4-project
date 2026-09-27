using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 사이렌 버튼 위에 남은 쿨다운을 월드 스페이스 게이지로 표시한다.
/// </summary>
public class JailSirenButtonView : MonoBehaviour
{
    [Tooltip("비우면 같은 오브젝트에서 자동 탐색")]
    [SerializeField]
    private JailSirenButton m_button;

    [Tooltip("Image Type: Filled — 쿨다운 동안 0→100%로 차오른다")]
    [SerializeField]
    private Image m_fillImage;

    private float m_duration;
    private float m_elapsed;
    private bool m_isRunning;

    private void Awake()
    {
        if (m_button == null)
            m_button = GetComponent<JailSirenButton>();

        SetVisible(false);
    }

    private void OnEnable()
    {
        if (m_button != null)
            m_button.OnCooldownStarted += HandleCooldownStarted;
    }

    private void OnDisable()
    {
        if (m_button != null)
            m_button.OnCooldownStarted -= HandleCooldownStarted;
    }

    private void HandleCooldownStarted(float remaining)
    {
        m_duration = m_button.CooldownSeconds;
        if (m_duration <= 0f || remaining <= 0f)
            return;

        m_elapsed = Mathf.Clamp(m_duration - remaining, 0f, m_duration);
        m_isRunning = true;
        Apply();
        SetVisible(true);
    }

    private void Update()
    {
        if (!m_isRunning)
            return;

        m_elapsed += Time.deltaTime;
        Apply();

        if (m_elapsed >= m_duration)
            Stop();
    }

    private void Stop()
    {
        m_isRunning = false;
        SetVisible(false);
    }

    private void Apply()
    {
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);
    }

    private void SetVisible(bool visible)
    {
        if (m_fillImage != null)
            m_fillImage.enabled = visible;
    }
}
