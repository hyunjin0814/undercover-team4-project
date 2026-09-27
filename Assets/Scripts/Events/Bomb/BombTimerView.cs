using TMPro;
using UnityEngine;

/// <summary>
/// 폭탄에 붙는 월드 텍스트로 남은 시간을 매 프레임 표시한다.
/// </summary>
public class BombTimerView : MonoBehaviour
{
    [Tooltip("카운트다운을 그릴 텍스트 — 비우면 이 오브젝트의 TMP_Text를 쓴다")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("이 시간(초) 이하로 남으면 경고색으로 바뀐다")]
    [SerializeField]
    private float m_warnThreshold = 10f;

    [SerializeField]
    private Color m_normalColor = new Color(0.2f, 1f, 0.35f);

    [SerializeField]
    private Color m_warnColor = new Color(1f, 0.25f, 0.2f);

    private BombDevice m_device;

    private void Awake()
    {
        if (m_label == null)
            m_label = GetComponent<TMP_Text>();
        m_device = GetComponentInParent<BombDevice>();
    }

    private void Update()
    {
        if (m_label == null || m_device == null)
            return;

        switch (m_device.State)
        {
            case BombState.Armed:
                float t = m_device.RemainingSeconds;
                int minutes = (int)(t / 60f);
                int seconds = (int)(t % 60f);
                m_label.text = string.Format("{0}:{1:00}", minutes, seconds);
                m_label.color = t <= m_warnThreshold ? m_warnColor : m_normalColor;
                break;

            case BombState.Exploded:
                m_label.text = "BOOM";
                m_label.color = m_warnColor;
                break;

            default:
                m_label.text = "--:--";
                m_label.color = m_normalColor;
                break;
        }
    }
}
