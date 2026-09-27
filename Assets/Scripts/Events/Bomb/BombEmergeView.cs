using UnityEngine;

/// <summary>
/// 폭탄 등장 연출 — 상태가 Emerging이 되면 로봇이 들썩이며 기동하는 애니메이션을 로컬로 재생한다.
/// </summary>
[RequireComponent(typeof(BombDevice))]
public class BombEmergeView : MonoBehaviour
{
    [Tooltip("폭탄 본체 모델 — 기동할 때 살짝 들썩인다")]
    [SerializeField]
    private Transform m_model;

    [Tooltip("상자가 물러날 때까지 기다리는 시간(초) — BombCrate의 들썩+밀려남 길이에 맞출 것. " +
             "짧으면 아직 상자에 덮인 채로 들썩이고, 길면 드러난 뒤 한참 가만히 있는다")]
    [SerializeField]
    private float m_startupDelaySeconds = 1.25f;

    [Tooltip("로봇이 기동하며 들썩이는 높이(m)")]
    [SerializeField]
    private float m_startupHop = 0.12f;

    private BombDevice m_device;
    private bool m_playing;
    private float m_elapsed;
    private Vector3 m_modelPosition;

    private void Awake()
    {
        m_device = GetComponent<BombDevice>();
        if (m_model != null)
            m_modelPosition = m_model.localPosition;
    }

    private void Update()
    {
        if (m_model == null)
            return;

        if (m_device.State != BombState.Emerging)
        {
            if (m_playing)
            {
                m_playing = false;
                m_model.localPosition = m_modelPosition;
            }
            return;
        }

        if (!m_playing)
        {
            m_playing = true;
            m_elapsed = 0f;
        }

        m_elapsed += Time.deltaTime;
        if (m_elapsed < m_startupDelaySeconds)
            return;

        TickStartup(m_elapsed - m_startupDelaySeconds);
    }

    private void TickStartup(float t)
    {
        float decay = Mathf.Exp(-t * 5f);
        float hop = Mathf.Sin(t * 12f) * m_startupHop * decay;
        m_model.localPosition = m_modelPosition + Vector3.up * Mathf.Max(0f, hop);
    }
}
