using System;
using System.Collections.Generic;
using Unity.Services.Vivox;
using UnityEngine;

/// <summary>
/// 먹통 중 Vivox 오디오 탭으로 참가자 음성을 AudioSource로 끌어와 왜곡 필터를 건다(로컬 처리).
/// VivoxManager의 부품으로, 음색은 VoiceDistortionProfile이 정한다.
/// </summary>
public class VoiceDistortionController : MonoBehaviour
{
    [Tooltip(
        "왜곡 음색 프로파일(SO) — 필터 조합·수치는 전부 이 에셋이 정한다. 비면 왜곡을 걸지 않는다"
    )]
    [SerializeField]
    private VoiceDistortionProfile m_profile;

    [Tooltip(
        "근접 채널 음성도 왜곡할지. 끄면 무전 채널만 왜곡한다 — 탭으로 빼낸 뒤에도 Vivox의 3D 감쇠가 "
            + "유지되는지 확인이 필요하다(멀리 있는 사람이 크게 들리면 끌 것)"
    )]
    [SerializeField]
    private bool m_distortProximityToo = true;

    private bool m_distorted;
    private float m_nextGlitchTime;

    private bool m_channelsJoined;
    private string m_proximityChannelName;

    private readonly Dictionary<VivoxParticipant, AudioSource> m_taps = new();

    public bool IsDistorted => m_distorted;

    /// <summary>왜곡을 켜고 끈다 — <see cref="DeviceBlackoutView"/> → VivoxManager 위임 경로.</summary>
    public void SetDistorted(bool distorted)
    {
        if (m_distorted == distorted)
            return;

        if (distorted && m_profile == null)
        {
            Debug.LogWarning(
                "[VoiceDistortionController] VoiceDistortionProfile 미할당 — 왜곡을 건너뛴다",
                this
            );
            return;
        }

        m_distorted = distorted;

        if (distorted)
            ApplyToAll();
        else
            ClearAll();
    }

    /// <summary>왜곡 재생 AudioSource에 설정의 음성 음량을 적용한다.</summary>
    public void ApplyVolume()
    {
        float volume = GameSettings.VoiceVolume;

        foreach (AudioSource source in m_taps.Values)
        {
            if (source != null)
                source.volume = volume;
        }
    }

    /// <summary>참가자 입장 통보 — 먹통 중에 들어온 사람도 왜곡을 받아야 한다.</summary>
    public void HandleParticipantAdded(VivoxParticipant participant)
    {
        if (m_distorted && ShouldDistortChannel(participant.ChannelName))
            Apply(participant);
    }

    /// <summary>참가자 퇴장 통보 — 탭 오브젝트는 Vivox가 함께 정리하므로 기록만 지운다.</summary>
    public void HandleParticipantRemoved(VivoxParticipant participant) =>
        m_taps.Remove(participant);

    /// <summary>채널 참가 완료 — 먹통 중 재참가면 이미 들어와 있는 참가자에게도 다시 건다.</summary>
    public void NotifyChannelsJoined(string proximityChannelName)
    {
        m_proximityChannelName = proximityChannelName;
        m_channelsJoined = true;

        if (m_distorted)
            ApplyToAll();
    }

    /// <summary>채널 이탈 — 탭은 참가자와 함께 사라지므로 기록만 비운다. 플래그는 유지(재참가 대비).</summary>
    public void NotifyChannelsLeft()
    {
        m_channelsJoined = false;
        m_taps.Clear();
    }

    /// <summary>음성 종료(로그아웃·세션 이탈) — 먹통 맥락이 사라지므로 플래그까지 내린다.</summary>
    public void NotifyVoiceEnded()
    {
        m_distorted = false;
        m_channelsJoined = false;
        m_taps.Clear();
    }

    private void ApplyToAll()
    {
        if (!m_channelsJoined)
            return;

        foreach (var channel in VivoxService.Instance.ActiveChannels)
        {
            if (!ShouldDistortChannel(channel.Key))
                continue;

            foreach (VivoxParticipant participant in channel.Value)
                Apply(participant);
        }
    }

    private bool ShouldDistortChannel(string channelName) =>
        m_distortProximityToo || channelName != m_proximityChannelName;

    private void Apply(VivoxParticipant participant)
    {
        if (participant == null || participant.IsSelf)
            return;
        if (m_profile == null)
            return;
        if (m_taps.ContainsKey(participant))
            return;

        bool tapCreated = false;

        try
        {
            GameObject tapObject = participant.CreateVivoxParticipantTap(
                $"BlackoutVoiceTap_{participant.PlayerId}",
                true
            );
            tapCreated = true;

            AudioSource source = participant.ParticipantTapAudioSource;
            if (tapObject == null || source == null)
            {
                Debug.LogWarning(
                    $"[VoiceDistortionController] 탭 생성 실패 — {participant.PlayerId}"
                );
                SafeDestroyTap(participant);
                return;
            }

            m_profile.Apply(tapObject, source);
            source.volume = GameSettings.VoiceVolume;

            m_taps[participant] = source;
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[VoiceDistortionController] 왜곡 적용 실패 ({participant.PlayerId}): {ex}"
            );

            if (tapCreated && !m_taps.ContainsKey(participant))
                SafeDestroyTap(participant);
        }
    }

    private void SafeDestroyTap(VivoxParticipant participant)
    {
        if (participant == null)
            return;

        try
        {
            participant.DestroyVivoxParticipantTap();
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[VoiceDistortionController] 탭 해제 실패 ({participant.PlayerId}): {ex}"
            );
        }
    }

    private void ClearAll()
    {
        foreach (VivoxParticipant participant in new List<VivoxParticipant>(m_taps.Keys))
            SafeDestroyTap(participant);

        m_taps.Clear();
    }

    private void Update()
    {
        if (!m_distorted || m_taps.Count == 0)
            return;
        if (Time.time < m_nextGlitchTime)
            return;

        m_nextGlitchTime = Time.time + m_profile.NextGlitchInterval();
        float pitch = m_profile.NextGlitchPitch();

        foreach (AudioSource source in m_taps.Values)
            if (source != null)
                source.pitch = pitch;
    }
}
