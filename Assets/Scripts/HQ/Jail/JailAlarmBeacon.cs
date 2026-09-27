using UnityEngine;

/// <summary>
/// 유치장 경보등 — 자물쇠 해제 시도와 탈옥을 색과 점멸로 알리고 일정 시간 뒤 가라앉는다.
/// 각 피어가 JailLock 신호를 구독해 스스로 켠다.
/// </summary>
public class JailAlarmBeacon : MonoBehaviour
{
    private enum EState
    {
        Idle,
        Attempt,
        Opened,
    }

    [Header("대상 자물쇠 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private JailLock m_jailLock;

    [Header("빛나는 부분")]
    [Tooltip("색을 바꿀 렌더러 — 경광등 유리/램프 부분. 여러 개면 전부 같이 바뀐다")]
    [SerializeField]
    private Renderer[] m_renderers;

    [Tooltip("같이 켤 실광원 (선택) — 없으면 렌더러 색만 바뀐다")]
    [SerializeField]
    private Light m_light;

    [Header("상태별 색")]
    [SerializeField]
    private Color m_idleColor = new Color(0.15f, 0.15f, 0.15f);

    [Tooltip("해제 시도 감지 — 주의")]
    [SerializeField]
    private Color m_attemptColor = new Color(1f, 0.7f, 0.1f);

    [Tooltip("탈옥 — 경보")]
    [SerializeField]
    private Color m_openedColor = new Color(1f, 0.15f, 0.1f);

    [Header("연출")]
    [Tooltip("초당 점멸 횟수")]
    [SerializeField]
    private float m_blinkPerSecond = 2f;

    [Tooltip("해제 시도 경보가 저절로 가라앉기까지의 시간(초)")]
    [SerializeField]
    private float m_attemptSeconds = 6f;

    [Tooltip("탈옥 경보가 저절로 가라앉기까지의 시간(초)")]
    [SerializeField]
    private float m_openedSeconds = 10f;

    [Tooltip("실광원의 최대 밝기 — 점멸에 따라 0~이 값 사이를 오간다")]
    [SerializeField]
    private float m_lightIntensity = 4f;

    private MaterialPropertyBlock m_block;
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int s_emissionColorId = Shader.PropertyToID("_EmissionColor");

    private EState m_state = EState.Idle;
    private float m_stateUntil;

    private bool m_knownLocked = true;

    public bool IsAttemptAlarming => m_state == EState.Attempt;

    private void Awake()
    {
        m_block = new MaterialPropertyBlock();
    }

    private void Start()
    {
        if (m_jailLock == null)
            m_jailLock = App.Game.JailLock;

        if (m_jailLock == null)
        {
            Debug.LogWarning("JailAlarmBeacon: JailLock을 찾지 못해 경보등이 동작하지 않는다", this);
            enabled = false;
            return;
        }

        m_jailLock.OnLockChanged += HandleLockChanged;
        m_jailLock.OnUnlockAttempt += HandleUnlockAttempt;

        m_knownLocked = m_jailLock.IsLocked;
        m_state = EState.Idle;
    }

    private void OnDestroy()
    {
        if (m_jailLock == null)
            return;

        m_jailLock.OnLockChanged -= HandleLockChanged;
        m_jailLock.OnUnlockAttempt -= HandleUnlockAttempt;
    }

    private void HandleLockChanged(bool locked)
    {
        bool wasLocked = m_knownLocked;
        m_knownLocked = locked;

        if (locked)
        {
            m_state = EState.Idle;
            return;
        }

        if (!wasLocked)
            return;

        m_state = EState.Opened;
        m_stateUntil = Time.time + m_openedSeconds;
    }

    private void HandleUnlockAttempt()
    {
        if (m_state == EState.Opened)
            return;

        m_state = EState.Attempt;
        m_stateUntil = Time.time + m_attemptSeconds;
    }

    private void Update()
    {
        if (m_state != EState.Idle && Time.time >= m_stateUntil)
            m_state = EState.Idle;

        Apply();
    }

    private void Apply()
    {
        Color target = StateColor();

        float blink = m_state == EState.Idle
            ? 1f
            : Mathf.PingPong(Time.time * m_blinkPerSecond * 2f, 1f);

        Color lit = target * blink;

        if (m_renderers != null)
        {
            foreach (Renderer renderer in m_renderers)
            {
                if (renderer == null)
                    continue;

                renderer.GetPropertyBlock(m_block);
                m_block.SetColor(s_baseColorId, lit);
                m_block.SetColor(s_emissionColorId, lit);
                renderer.SetPropertyBlock(m_block);
            }
        }

        if (m_light != null)
        {
            m_light.color = target;
            m_light.intensity = m_state == EState.Idle ? 0f : m_lightIntensity * blink;
        }
    }

    private Color StateColor()
    {
        switch (m_state)
        {
            case EState.Opened:
                return m_openedColor;
            case EState.Attempt:
                return m_attemptColor;
            default:
                return m_idleColor;
        }
    }
}
