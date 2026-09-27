using UnityEngine;

/// <summary>
/// 폭탄 카운트다운 삐 소리 — 남은 시간에 따라 느린/빠른 삐를 자체 AudioSource로 반복 재생한다.
/// 각 피어가 매 프레임 BombDevice를 읽어 같은 소리를 낸다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class BombCountdownSound : MonoBehaviour
{
    [Tooltip("평상시 카운트 — 느린 삐(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_slowSound = EAudioClip.BombCountdownSlow;

    [Tooltip("임박 — 빠른 삐(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_fastSound = EAudioClip.BombCountdownFast;

    [Tooltip("남은 시간이 이 값(초) 이하면 빠른 삐로 바꾼다. 폭탄이 그 자리에 멈추는 시각" +
             "(BombDevice의 잠금 시간, 기본 3초)과 맞춰 두면 소리가 '폭심이 확정됐다'를 알린다")]
    [Min(0f)]
    [SerializeField]
    private float m_fastThresholdSeconds = 3f;

    private BombDevice m_device;
    private AudioSource m_source;
    private EAudioClip m_playing = EAudioClip.None;

    private void Awake()
    {
        m_device = GetComponentInParent<BombDevice>();

        m_source = GetComponent<AudioSource>();
        m_source.playOnAwake = false;
        m_source.loop = true;
        m_source.spatialBlend = 1f;
        m_source.rolloffMode = AudioRolloffMode.Linear;
    }

    private void OnEnable()
    {
        if (m_device != null)
            m_device.OnExploded += HandleExploded;
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnExploded -= HandleExploded;

        Stop();
    }

    private void HandleExploded() => Stop();

    private void Update()
    {
        if (m_device == null || !m_device.IsCountingDown)
        {
            Stop();
            return;
        }

        Play(m_device.RemainingSeconds <= m_fastThresholdSeconds ? m_fastSound : m_slowSound);
    }

    private void Play(EAudioClip id)
    {
        if (m_playing == id)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(id);
        if (entry?.Clip == null)
        {
            Stop();
            return;
        }

        m_source.clip = entry.Clip;
        m_source.volume = SoundManager.SfxVolumeOf(entry);
        m_source.minDistance = entry.MinDistance;
        m_source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_source.Play();
        m_playing = id;
    }

    private void Stop()
    {
        if (m_playing == EAudioClip.None)
            return;

        m_source.Stop();
        m_source.clip = null;
        m_playing = EAudioClip.None;
    }
}
