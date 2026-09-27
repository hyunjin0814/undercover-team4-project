using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 정해진 시간이 지나면 페이드되며 사라지는 표시 베이스(경보·수신용).
/// </summary>
public abstract class TimedMessageView : LocalizedMessageView
{
    [Tooltip("사라지기 직전 이 시간(초) 동안 서서히 투명해진다")]
    [SerializeField]
    private float m_fadeSeconds = 1f;

    private float m_hideTime;

    /// <summary>문구를 seconds초 동안 표시한다. 이미 떠 있으면 덮어쓴다.</summary>
    public void Show(LocalizedString message, float seconds) => Show(message, seconds, null);

    /// <summary>배경색을 지정해 문구를 seconds초 동안 표시한다.</summary>
    public void Show(LocalizedString message, float seconds, Color? tone)
    {
        if (seconds <= 0f)
            return;

        ApplyTone(tone);
        ShowMessage(message);
        m_hideTime = Time.time + seconds;
    }

    private void Update()
    {
        if (!IsShowing)
            return;

        float remaining = m_hideTime - Time.time;
        if (remaining <= 0f)
        {
            HideImmediate();
            return;
        }

        if (m_fadeSeconds > 0f && Group != null)
            Group.alpha = Mathf.Clamp01(remaining / m_fadeSeconds);
    }
}
