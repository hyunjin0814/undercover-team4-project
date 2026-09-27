using UnityEngine;

/// <summary>
/// 유치장 경보등 사이렌 — 해제 시도 경보에만 3D 루프음을 자체 AudioSource로 재생한다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class JailAlarmBeaconSound : MonoBehaviour
{
    [Tooltip("해제 시도 경보가 도는 동안 계속 낼 소리(루프). 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_sound = EAudioClip.JailAlarm;

    [Tooltip("대상 경보등 (비우면 자신·부모에서 찾는다)")]
    [SerializeField]
    private JailAlarmBeacon m_beacon;

    private AudioSource m_source;
    private bool m_playing;

    private void Awake()
    {
        if (m_beacon == null)
            m_beacon = GetComponentInParent<JailAlarmBeacon>();

        m_source = GetComponent<AudioSource>();
        m_source.playOnAwake = false;
        m_source.loop = true;
        m_source.spatialBlend = 1f;
        m_source.rolloffMode = AudioRolloffMode.Linear;

        if (m_beacon == null)
        {
            Debug.LogWarning("JailAlarmBeaconSound: JailAlarmBeacon을 찾지 못해 사이렌이 울리지 않는다", this);
            enabled = false;
        }
    }

    private void OnEnable() => GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;

    private void OnDisable()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        Stop();
    }

    private void HandleSfxVolumeChanged(float _)
    {
        if (m_source != null && m_playing)
            m_source.volume = SoundManager.SfxVolumeOf(App.Sound?.GetSfxEntry(m_sound));
    }

    private void Update()
    {
        if (m_beacon.IsAttemptAlarming)
            Play();
        else
            Stop();
    }

    private void Play()
    {
        if (m_playing)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_sound);
        if (entry?.Clip == null)
            return;

        m_source.clip = entry.Clip;
        m_source.volume = SoundManager.SfxVolumeOf(entry);
        m_source.minDistance = entry.MinDistance;
        m_source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_source.Play();
        m_playing = true;
    }

    private void Stop()
    {
        if (!m_playing)
            return;

        m_source.Stop();
        m_source.clip = null;
        m_playing = false;
    }
}
