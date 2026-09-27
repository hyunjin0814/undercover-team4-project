using UnityEngine;

/// <summary>
/// 발소리 루프와 이륙·착지음을 재생한다 — 각 피어가 모든 플레이어의 이동량을 보고 스스로 낸다.
/// 발소리는 자체 AudioSource 루프로, 이륙·착지는 효과음 풀로 재생한다.
/// </summary>
[RequireComponent(typeof(PlayerJump))]
public class PlayerFootstepView : MonoBehaviour
{
    [Header("이동 판정 (인스펙터 조절)")]
    [Tooltip("이 속도(m/s) 아래면 멈춘 것으로 보고 발소리를 끈다 — 경사에서 미끄러지는 정도는 걷는 것이 아니다")]
    [SerializeField]
    private float m_moveSpeedThreshold = 0.6f;

    [Tooltip("이 속도(m/s)를 넘으면 뛰는 것으로 보고 뜀 발소리로 바꾼다 — 걷기 5·달리기 8 사이에 둘 것")]
    [SerializeField]
    private float m_runSpeedThreshold = 6.5f;

    [Tooltip("속도를 얼마나 부드럽게 볼지(초). 0에 가까울수록 즉각 반응하지만 한 프레임 튐에도 소리가 깜빡인다")]
    [Min(0f)]
    [SerializeField]
    private float m_speedSmoothing = 0.12f;

    private PlayerJump m_jump;
    private PlayerHealth m_health;
    private AudioSource m_loopSource;

    private Vector3 m_lastPosition;
    private float m_speed;
    private bool m_wasAirborne;
    private EAudioClip m_loopId = EAudioClip.None;

    private void Awake()
    {
        m_jump = GetComponent<PlayerJump>();
        m_health = GetComponent<PlayerHealth>();

        var host = new GameObject("FootstepLoop");
        host.transform.SetParent(transform, false);

        m_loopSource = host.AddComponent<AudioSource>();
        m_loopSource.playOnAwake = false;
        m_loopSource.loop = true;
        m_loopSource.spatialBlend = 1f;
        m_loopSource.rolloffMode = AudioRolloffMode.Linear;
    }

    private void OnEnable()
    {
        m_lastPosition = transform.position;
        m_speed = 0f;
        m_wasAirborne = m_jump.IsAirborne;
        GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;
    }

    private void OnDisable()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        StopLoop();
    }

    private void HandleSfxVolumeChanged(float _)
    {
        if (m_loopSource != null && m_loopId != EAudioClip.None)
            m_loopSource.volume = SoundManager.SfxVolumeOf(App.Sound?.GetSfxEntry(m_loopId));
    }

    private void Update()
    {
        Vector3 delta = transform.position - m_lastPosition;
        m_lastPosition = transform.position;
        delta.y = 0f;

        float instant = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
        m_speed = m_speedSmoothing > 0f
            ? Mathf.Lerp(m_speed, instant, Time.deltaTime / m_speedSmoothing)
            : instant;

        bool airborne = m_jump.IsAirborne;
        if (airborne != m_wasAirborne)
        {
            m_wasAirborne = airborne;
            PlayOneShot(airborne ? EAudioClip.JumpTakeoff : EAudioClip.JumpLand);
        }

        if (airborne)
        {
            StopLoop();
            return;
        }

        if (m_health != null && !m_health.IsTargetable)
        {
            StopLoop();
            return;
        }

        if (m_speed < m_moveSpeedThreshold)
        {
            StopLoop();
            return;
        }

        SetLoop(m_speed > m_runSpeedThreshold ? EAudioClip.FootstepRun : EAudioClip.FootstepWalk);
    }

    private void SetLoop(EAudioClip id)
    {
        if (m_loopId == id)
            return;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(id);
        if (entry?.Clip == null)
        {
            StopLoop();
            return;
        }

        m_loopSource.clip = entry.Clip;
        m_loopSource.volume = SoundManager.SfxVolumeOf(entry);
        m_loopSource.minDistance = entry.MinDistance;
        m_loopSource.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        m_loopSource.Play();
        m_loopId = id;
    }

    private void StopLoop()
    {
        if (m_loopId == EAudioClip.None)
            return;

        m_loopSource.Stop();
        m_loopId = EAudioClip.None;
    }

    private void PlayOneShot(EAudioClip id) => App.Sound?.PlaySfxAt(id, transform.position);
}
