using UnityEngine;

/// <summary>
/// 유치장 경보등 — 본부에 두는 물리 오브젝트로, 자물쇠에서 벌어지는 일을 색과 점멸로 알린다. (#311/#493)
///
/// 화면 텍스트 경보를 대신한다. 본부 담당자가 <b>화면이 아니라 공간</b>을 보고 알아채게 하는 것이
/// 목적이라, 어디를 보고 있든 뜨는 HUD와 달리 "경보등 쪽을 봐야 안다". 대신 무전으로 현장에
/// 알리는 행위가 필요해진다 — 본부/현장 분업이라는 게임의 축과 맞는다.
///
/// 두 경보 모두 <b>정해진 시간 뒤 저절로 가라앉는다</b> — "사건이 터졌다"를 알리는 것이 목적이고,
/// "문이 아직 열려 있다"를 계속 표시하는 것이 아니다. 자물쇠는 새 수감자를 받을 때만 다시 잠기므로
/// (<see cref="JailZone"/>) 열림 상태를 그대로 반영하면 라운드 끝까지 깜빡일 수 있다.
/// 탈옥이 시도보다 강하다 — 이미 열린 뒤에 온 시도 알림이 경보를 약하게 만들면 안 된다.
///
/// 서버·클라이언트 구분이 없다: 구독하는 두 신호가 이미 전 피어에서 발생하므로
/// (<see cref="JailLock.OnLockChanged"/>는 NetworkVariable 콜백, <see cref="JailLock.OnUnlockAttempt"/>는
/// ClientRpc) 각 피어가 자기 화면의 경보등을 각자 켠다.
/// </summary>
public class JailAlarmBeacon : MonoBehaviour
{
    private enum EState
    {
        Idle,    // 평시 — 램프 꺼짐
        Attempt, // 해제 시도 감지
        Opened,  // 탈옥(자물쇠 열림)
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

    // 색을 머티리얼에 직접 쓰면 인스턴스가 복제된다 — 프로퍼티 블록으로 렌더러에만 덮어쓴다
    private MaterialPropertyBlock m_block;
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int s_emissionColorId = Shader.PropertyToID("_EmissionColor");

    private EState m_state = EState.Idle;
    private float m_stateUntil;

    // 마지막으로 알고 있던 잠금 상태 — 잠김→열림 '전이'만 경보로 취급하기 위한 기준선.
    // 이게 없으면 라운드 도중 들어온 피어가 이미 열린 자물쇠의 초기 동기화를 새 탈옥으로 오인해
    // 지난 사건에 경보를 울린다.
    private bool m_knownLocked = true;

    private void Awake()
    {
        m_block = new MaterialPropertyBlock();
    }

    private void Start()
    {
        // 비워두면 App에서 받는다 (#592) — 감옥 시설은 실행 순서가 앞서 있어 여기서 이미 읽힌다
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

        // 현재 상태를 기준선으로만 잡는다 — 경보는 울리지 않는다.
        // 늦게 붙었을 때 상태를 즉시 반영하는 DeviceBlackoutView와 다른 이유: 저쪽은 지속 상태(음성 왜곡)라
        // 지금 값이 곧 정답이지만, 이쪽은 사건 알림이라 이미 지난 탈옥을 새로 울릴 이유가 없다.
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

        // 재잠금 = 상황 종료. 남은 경보 시간을 이어가지 않고 즉시 평시로 돌린다.
        if (locked)
        {
            m_state = EState.Idle;
            return;
        }

        // 이미 열려 있던 것을 다시 통보받은 경우(초기 동기화 등)는 새 사건이 아니다
        if (!wasLocked)
            return;

        m_state = EState.Opened;
        m_stateUntil = Time.time + m_openedSeconds;
    }

    private void HandleUnlockAttempt()
    {
        // 탈옥 경보가 울리는 중이면 격하하지 않는다 — 이미 열린 쪽이 더 심각하다
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

        // 평시엔 점멸하지 않는다 — 꺼진 램프가 곧 "이상 없음"이다
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
