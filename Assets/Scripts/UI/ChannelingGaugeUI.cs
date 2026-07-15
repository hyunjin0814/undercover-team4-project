using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 채널링(체포·구조·스캔) 진행을 표시하는 화면 중앙 원형 게이지. (#184)
/// HUD 프리팹에 부착되며 로컬 싱글턴으로 접근한다 — 크로스헤어(CrosshairUI)와 동일 관례.
/// 서버가 시작(지속시간)/종료 이벤트만 알리고(각 채널링 호스트의 오너 RPC),
/// 진행률은 로컬 시간으로 채운다 — 진행도를 매 프레임 동기화하지 않기 위함.
/// </summary>
public class ChannelingGaugeUI : MonoBehaviour
{
    [Tooltip("원형 필 이미지 — Image Type: Filled / Radial 360")]
    [SerializeField]
    private Image m_fillImage;

    public static ChannelingGaugeUI Instance { get; private set; }

    private float m_duration;
    private float m_elapsed;
    private bool m_isRunning;

    private void Awake()
    {
        Instance = this;
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>채널링 시작 — seconds 동안 0→100%로 차오른다. 서버 시작 통지 수신 시 호출.</summary>
    public void Show(float seconds)
    {
        if (seconds <= 0f)
            return;

        m_duration = seconds;
        m_elapsed = 0f;
        m_isRunning = true;
        if (m_fillImage != null)
            m_fillImage.fillAmount = 0f;
        SetVisible(true);
    }

    /// <summary>채널링 종료(완료·취소·실패 공통) — 게이지를 즉시 숨긴다.</summary>
    public void Hide()
    {
        m_isRunning = false;
        SetVisible(false);
    }

    private void Update()
    {
        if (!m_isRunning)
            return;

        m_elapsed += Time.deltaTime;
        if (m_fillImage != null)
            m_fillImage.fillAmount = Mathf.Clamp01(m_elapsed / m_duration);

        // 서버 종료 통지가 유실·지연돼도 게이지가 화면에 눌어붙지 않게 완료 후 여유를 두고 자동 숨김
        if (m_elapsed >= m_duration + 0.5f)
            Hide();
    }

    private void SetVisible(bool visible)
    {
        if (m_fillImage != null)
            m_fillImage.gameObject.SetActive(visible);
    }
}
