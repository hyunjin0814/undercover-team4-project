using UnityEngine;

/// <summary>
/// 먹통 음성 왜곡의 필터 조합과 수치를 담은 SO.
/// VivoxManager는 언제 왜곡할지만 알고, 어떻게 왜곡할지는 이 에셋이 정한다.
/// </summary>
[CreateAssetMenu(
    fileName = "VoiceDistortionProfile",
    menuName = "Undercover/Voice/Distortion Profile"
)]
public class VoiceDistortionProfile : ScriptableObject
{
    [Header("기본 음색")]
    [Tooltip("왜곡 기본 피치 — 1보다 낮으면 저음으로 뭉개진다 (고장난 음성 합성기 느낌)")]
    [SerializeField, Range(0.4f, 1.5f)]
    private float m_basePitch = 0.6f;

    [Tooltip("왜곡 강도 (AudioDistortionFilter) — 높을수록 지직거린다")]
    [SerializeField, Range(0f, 1f)]
    private float m_distortionLevel = 0.35f;

    [Tooltip(
        "저역 통과 차단 주파수(Hz) — 낮을수록 먹먹해진다. 링 모듈레이터를 쓸 때는 너무 낮추지 말 것: "
            + "기계음의 특징인 금속성 배음까지 깎여 그냥 먹먹한 소리가 된다"
    )]
    [SerializeField, Range(300f, 5000f)]
    private float m_lowPassHz = 2000f;

    [Header("기계음 (링 모듈레이션)")]
    [Tooltip("링 모듈레이터(기계음) 사용 — 로봇이 말하는 듯한 음색. 기계음 연출의 핵심")]
    [SerializeField]
    private bool m_useRingMod = true;

    [Tooltip("링 모듈레이터 반송파 주파수(Hz). 30~80이 전형적인 로봇 음성, 높일수록 금속성 링잉에 가까워진다")]
    [SerializeField, Range(10f, 400f)]
    private float m_ringModCarrierHz = 55f;

    [Header("피치 글리치")]
    [Tooltip("피치가 튀는 간격(초) 최소/최대 — 짧을수록 자주 튀어 알아듣기 어려워진다")]
    [SerializeField]
    private float m_glitchIntervalMin = 0.22f;

    [SerializeField]
    private float m_glitchIntervalMax = 0.65f;

    [Tooltip("글리치 피치를 반음 단위로 스냅 — 오토튠 특유의 계단식 음정 변화를 만든다")]
    [SerializeField]
    private bool m_snapPitchToSemitones = true;

    [Tooltip("글리치 시 기본 피치에서 벗어나는 반음 범위 (-7 = 5도 아래, +7 = 5도 위)")]
    [SerializeField]
    private int m_glitchSemitoneMin = -7;

    [SerializeField]
    private int m_glitchSemitoneMax = 7;

    [Tooltip("반음 스냅을 끈 경우에 쓰는 연속 피치 범위")]
    [SerializeField]
    private float m_glitchPitchMin = 0.55f;

    [SerializeField]
    private float m_glitchPitchMax = 1.5f;

    [Header("코러스 (겹침 흔들림)")]
    [Tooltip("코러스 사용 — 말을 뭉갠다. 링 모듈레이터와 함께 켜면 과해지기 쉽다")]
    [SerializeField]
    private bool m_useChorus;

    [Tooltip("코러스 깊이 — 높을수록 흔들림이 커진다")]
    [SerializeField, Range(0f, 1f)]
    private float m_chorusDepth = 0.7f;

    [Tooltip("코러스 속도(Hz) — 흔들리는 빠르기")]
    [SerializeField, Range(0f, 20f)]
    private float m_chorusRate = 1.2f;

    [Tooltip("코러스 혼합량 — 원음 대비 흔들린 복사본의 비중")]
    [SerializeField, Range(0f, 1f)]
    private float m_chorusMix = 0.6f;

    [Header("에코")]
    [Tooltip("짧은 에코 사용 — 소리를 번지게 해 뭉개짐을 더한다")]
    [SerializeField]
    private bool m_useEcho = true;

    [Tooltip("에코 딜레이(ms) — 짧을수록 금속성으로 번진다")]
    [SerializeField, Range(10f, 500f)]
    private float m_echoDelayMs = 45f;

    [Tooltip("에코 감쇠율 — 높을수록 오래 번진다")]
    [SerializeField, Range(0f, 1f)]
    private float m_echoDecay = 0.3f;

    [Tooltip("에코 혼합량")]
    [SerializeField, Range(0f, 1f)]
    private float m_echoMix = 0.35f;

    /// <summary>오디오 탭에 링모드 → 코러스 → 왜곡 → 에코 → 저역통과 순으로 필터를 얹는다.</summary>
    public void Apply(GameObject tapObject, AudioSource source)
    {
        if (tapObject == null)
            return;

        if (source != null)
            source.pitch = m_basePitch;

        if (m_useRingMod)
            tapObject.AddComponent<VoiceRingModulator>().Configure(m_ringModCarrierHz);

        if (m_useChorus)
        {
            AudioChorusFilter chorus = tapObject.AddComponent<AudioChorusFilter>();
            chorus.depth = m_chorusDepth;
            chorus.rate = m_chorusRate;
            chorus.wetMix1 = m_chorusMix;
            chorus.wetMix2 = m_chorusMix * 0.7f;
            chorus.wetMix3 = m_chorusMix * 0.4f;
        }

        AudioDistortionFilter distortion = tapObject.AddComponent<AudioDistortionFilter>();
        distortion.distortionLevel = m_distortionLevel;

        if (m_useEcho)
        {
            AudioEchoFilter echo = tapObject.AddComponent<AudioEchoFilter>();
            echo.delay = m_echoDelayMs;
            echo.decayRatio = m_echoDecay;
            echo.wetMix = m_echoMix;
            echo.dryMix = 1f;
        }

        AudioLowPassFilter lowPass = tapObject.AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = m_lowPassHz;
    }

    /// <summary>다음 글리치까지의 대기 시간(초).</summary>
    public float NextGlitchInterval() => Random.Range(m_glitchIntervalMin, m_glitchIntervalMax);

    /// <summary>다음 글리치 피치를 뽑는다. 반음 스냅이 켜져 있으면 반음 단위로만 변한다.</summary>
    public float NextGlitchPitch()
    {
        if (!m_snapPitchToSemitones)
            return Random.Range(m_glitchPitchMin, m_glitchPitchMax);

        int semitone = Random.Range(m_glitchSemitoneMin, m_glitchSemitoneMax + 1);
        return m_basePitch * Mathf.Pow(2f, semitone / 12f);
    }
}
