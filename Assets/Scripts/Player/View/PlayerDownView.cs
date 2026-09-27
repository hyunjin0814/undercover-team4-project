using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오너 전용 다운 유예 화면 어두워짐 — 남은 유예 시간에 비례해 화면을 어둡게 한다.
/// 사망 전이 순간에는 암전 유지 후 페이드하며 소리와 음성 출력·송신을 끈다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerDownView : NetworkBehaviour
{
    [Tooltip("완전 사망 순간 완전 암전(과 무음)을 그대로 유지하는 시간(초)")]
    [SerializeField]
    private float m_deathHoldSeconds = 1f;

    [Tooltip("암전 유지가 끝난 뒤 관전 카메라가 드러나기까지 페이드 시간(초)")]
    [SerializeField]
    private float m_deathFadeSeconds = 0.6f;

    private PlayerIncapacitation m_incapacitation;

    private bool IsLocalOwner => !IsSpawned || m_isLocalPlayer;

    private bool m_isLocalPlayer;

    public override void OnNetworkSpawn() => m_isLocalPlayer = IsOwner;

    private IncapacitationCause m_lastCause = IncapacitationCause.None;

    private float m_deathElapsed = -1f;

    private bool m_isMuted;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    public override void OnNetworkDespawn()
    {
        if (IsLocalOwner)
            App.UI.DamageVignette?.SetDownDarkness(0f);

        RestoreVolume();

        if (m_lastCause == IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(false);

        m_lastCause = IncapacitationCause.None;
        m_deathElapsed = -1f;

        base.OnNetworkDespawn();
    }

    private void Update()
    {
        if (!IsLocalOwner)
            return;

        if (m_incapacitation == null)
        {
            App.UI.DamageVignette?.SetDownDarkness(0f);
            return;
        }

        IncapacitationCause cause = m_incapacitation.Cause;

        if (cause == IncapacitationCause.Die && m_lastCause != IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(true);
        else if (cause != IncapacitationCause.Die && m_lastCause == IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(false);

        if (m_lastCause == IncapacitationCause.Down && cause == IncapacitationCause.Die)
        {
            m_deathElapsed = 0f;
            AudioListener.volume = 0f;
            App.Net.Vivox?.ForceMuteOutput();
            m_isMuted = true;
        }
        m_lastCause = cause;

        if (m_deathElapsed >= 0f && cause != IncapacitationCause.Die)
        {
            m_deathElapsed = -1f;
            RestoreVolume();
        }

        if (m_deathElapsed >= 0f)
        {
            m_deathElapsed += Time.deltaTime;

            if (m_deathElapsed < m_deathHoldSeconds)
            {
                App.UI.DamageVignette?.SetDownDarkness(1f);
                return;
            }

            RestoreVolume();

            float fadeElapsed = m_deathElapsed - m_deathHoldSeconds;
            float fadeRatio =
                m_deathFadeSeconds > 0f ? Mathf.Clamp01(fadeElapsed / m_deathFadeSeconds) : 1f;
            App.UI.DamageVignette?.SetDownDarkness(1f - fadeRatio);

            if (fadeRatio >= 1f)
                m_deathElapsed = -1f;

            return;
        }

        if (cause != IncapacitationCause.Down)
        {
            App.UI.DamageVignette?.SetDownDarkness(0f);
            return;
        }

        float total = m_incapacitation.DieAfterDownSeconds;
        float darkness = total > 0f ? 1f - m_incapacitation.RemainingUntilDie / total : 1f;
        App.UI.DamageVignette?.SetDownDarkness(darkness);
    }

    private void RestoreVolume()
    {
        if (!m_isMuted)
            return;

        AudioListener.volume = GameSettings.MasterVolume;
        App.Net.Vivox?.ApplyVoiceVolume();
        m_isMuted = false;
    }
}
