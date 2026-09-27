using System;
using System.Text;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Services.Vivox;
using Unity.Services.Authentication;
using System.Collections.Generic;

/// <summary>
/// Vivox 음성 매니저 — 로그인과 무전·근접 채널 참가, 발화 상태, 음량을 관리한다.
/// 먹통 왜곡·위치 보고·입력은 부품 컴포넌트에 위임한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class VivoxManager : CommonManagerBase
{
    [SerializeField] private string m_channelPrefix = "Radio";
    [SerializeField] private SessionManager m_session;
    [SerializeField] private VoiceDistortionController m_distortion;
    [SerializeField] private ProximityPositionReporter m_positionReporter;
    [SerializeField] private VoiceInputRouter m_input;
    private bool m_loggedIn;
    private bool m_starting;
    private string m_statusDetail = string.Empty;

    [Header("근접 음성 (positional)")]
    [SerializeField] private string m_proximityChannelPrefix = "Proximity";
    [SerializeField] private int m_conversationalDistance = 3;
    [SerializeField] private int m_audibleDistance = 15;
    [SerializeField] private float m_audioFadeIntensity = 1.0f;
    private bool m_radioJoined;
    private bool m_proximityJoined;
    private string m_proximityChannelName;

    private readonly Dictionary<string, bool> m_speakingByPlayer = new();
    private bool m_participantEventsHooked;
    public event Action<string, bool> OnSpeakingChanged;

    public bool IsSpeaking(string playerId) =>
        !string.IsNullOrEmpty(playerId)
        && m_speakingByPlayer.TryGetValue(playerId, out var speaking)
        && speaking;

    public EVoiceState VoiceState { get; private set; } = EVoiceState.Idle;

    public event Action<EVoiceState> OnVoiceStateChanged;

    /// <summary>음성 상태의 디버그 GUI용 문구를 돌려준다.</summary>
    public static string ToLabel(EVoiceState state) =>
        state switch
        {
            EVoiceState.LoggingIn => "음성 연결 중...",
            EVoiceState.Joining => "음성 채널 참가 중...",
            EVoiceState.Connected => "음성 연결됨",
            EVoiceState.Failed => "음성 연결 실패",
            _ => "음성 대기 중",
        };

    private void SetVoiceState(EVoiceState state, string detail = "")
    {
        m_statusDetail = detail;
        if (VoiceState == state) return;

        VoiceState = state;
        OnVoiceStateChanged?.Invoke(state);
    }

    protected override void Awake()
    {
        base.Awake();
        if (m_distortion == null)
            Debug.LogWarning("[VivoxManager] VoiceDistortionController 미할당 — 먹통 음성 왜곡이 걸리지 않는다", this);
        if (m_positionReporter == null)
            Debug.LogWarning("[VivoxManager] ProximityPositionReporter 미할당 — 근접 음성 거리 감쇠가 갱신되지 않는다", this);
        if (m_input == null)
            Debug.LogWarning("[VivoxManager] VoiceInputRouter 미할당 — 무전·마이크 음소거 키가 동작하지 않는다", this);
    }

    private void OnEnable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined += HandleSessionJoined;
            m_session.OnSessionLeft += HandleSessionLeft;
            m_session.OnConnectionLost += HandleConnectionLost;

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut += HandleAuthSignedOut;
        }
    }

    private void OnDisable()
    {
        if (m_session != null)
        {
            m_session.OnSessionJoined -= HandleSessionJoined;
            m_session.OnSessionLeft -= HandleSessionLeft;
            m_session.OnConnectionLost -= HandleConnectionLost;

            if (m_session.Auth != null)
                m_session.Auth.OnSignedOut -= HandleAuthSignedOut;
        }
    }

    private async UniTask EnsureLoggedInAsync()
    {
        if (m_loggedIn || m_starting) return;
        m_starting = true;

        try
        {
            if (m_session == null)
            {
                SetVoiceState(EVoiceState.Failed, "SessionManager 미할당");
                Debug.LogError("[VivoxManager] SessionManager 참조가 없습니다.");
                return;
            }

            await m_session.EnsureSignedInAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                SetVoiceState(EVoiceState.Failed, "로그인 안 됨 - 세션 인증 필요");
                return;
            }

            SetVoiceState(EVoiceState.LoggingIn, "Vivox 초기화 중");
            await VivoxService.Instance.InitializeAsync();

            SetVoiceState(EVoiceState.LoggingIn, "Vivox 로그인 중");
            await VivoxService.Instance.LoginAsync(
                new LoginOptions { DisplayName = AuthenticationService.Instance.PlayerId });

            m_loggedIn = true;
            m_input?.NotifyLoggedIn();
            HookParticipantEvents();

            SetVoiceState(EVoiceState.LoggingIn, "Vivox 로그인 완료 — 채널 참가 전");
        }
        catch (Exception ex)
        {
            SetVoiceState(EVoiceState.Failed, $"로그인 실패: {ex.Message}");
            Debug.LogError($"[VivoxManager] {ex}");
        }
        finally
        {
            m_starting = false;
        }
    }

    private async UniTask JoinChannelAsync(string sessionId)
    {
        await EnsureLoggedInAsync();
        if (!m_loggedIn) return;

        if (m_session == null || m_session.CurrentSession == null
            || !AuthenticationService.Instance.IsSignedIn)
        {
            SetVoiceState(EVoiceState.Idle, "세션·인증이 먼저 정리됨");
            return;
        }

        await LeaveChannelAsync();

        string radio = BuildChannelName(m_channelPrefix, sessionId);
        m_proximityChannelName = BuildChannelName(m_proximityChannelPrefix, sessionId);

        try
        {
            SetVoiceState(EVoiceState.Joining);

            await VivoxService.Instance.JoinGroupChannelAsync(radio, ChatCapability.AudioOnly);
            m_radioJoined = true;

            var props = new Channel3DProperties(m_audibleDistance, m_conversationalDistance, m_audioFadeIntensity, AudioFadeModel.InverseByDistance);
            await VivoxService.Instance.JoinPositionalChannelAsync(m_proximityChannelName, ChatCapability.AudioOnly, props);
            m_proximityJoined = true;
            m_distortion?.NotifyChannelsJoined(m_proximityChannelName);
            m_positionReporter?.StartReporting(m_proximityChannelName);
            m_input?.NotifyChannelsJoined(m_proximityChannelName);

            ApplyMicMute();
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, m_proximityChannelName);

            ApplyVoiceVolume();

            SetVoiceState(EVoiceState.Connected);
        }
        catch (Exception ex)
        {
            SetVoiceState(EVoiceState.Failed, $"채널 참가 실패: {ex.Message}");
            Debug.LogError($"[VivoxManager] {ex}");
            await LeaveChannelAsync();
        }
    }

    private void HookParticipantEvents()
    {
        if (m_participantEventsHooked) return;
        VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
        m_participantEventsHooked = true;
    }

    private void UnhookParticipantEvents()
    {
        if (!m_participantEventsHooked) return;
        VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;
        m_participantEventsHooked = false;
    }

    private void OnParticipantAdded(VivoxParticipant participant)
    {
        participant.ParticipantSpeechDetected += () => RefreshSpeaking(participant.PlayerId);
        RefreshSpeaking(participant.PlayerId);

        m_distortion?.HandleParticipantAdded(participant);
    }

    private void OnParticipantRemoved(VivoxParticipant participant)
    {
        RefreshSpeaking(participant.PlayerId);

        m_distortion?.HandleParticipantRemoved(participant);
    }

    private void RefreshSpeaking(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return;

        bool speaking = false;
        foreach (var channel in VivoxService.Instance.ActiveChannels.Values)
        {
            foreach (var p in channel)
            {
                if (p.PlayerId == playerId && p.SpeechDetected) { speaking = true; break; }
            }
            if (speaking) break;
        }

        bool prev = m_speakingByPlayer.TryGetValue(playerId, out var v) && v;
        if (prev == speaking) return;

        m_speakingByPlayer[playerId] = speaking;
        OnSpeakingChanged?.Invoke(playerId, speaking);
    }

    private async UniTask LeaveChannelAsync()
    {
        if (!m_radioJoined && !m_proximityJoined) return;
        try
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VivoxManager] 채널 나가기 실패: {ex}");
        }
        finally
        {
            m_radioJoined = false;
            m_proximityJoined = false;
            m_positionReporter?.StopReporting();
            m_input?.NotifyChannelsLeft();
            m_distortion?.NotifyChannelsLeft();
        }
    }

    private string BuildChannelName(string prefix, string sessionId)
    {
        var sb = new StringBuilder(prefix);
        foreach (char c in sessionId)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public string PushToTalkBinding => m_input != null ? m_input.PushToTalkBinding : "(미할당)";

    public string MicMuteBinding => m_input != null ? m_input.MicMuteBinding : "(미할당)";

    public event Action OnMutedTalkAttempt
    {
        add { if (m_input != null) m_input.OnMutedTalkAttempt += value; }
        remove { if (m_input != null) m_input.OnMutedTalkAttempt -= value; }
    }

    /// <summary>설정의 음소거 값을 입력 장치에 적용한다 — GameSettings·채널 참가 두 곳이 부른다.</summary>
    public void ApplyMicMute() => m_input?.ApplyMicMute();

    private const int k_vivoxVolumeMute = -50;
    private const int k_vivoxVolumeFloor = -20;
    private const int k_vivoxVolumeCeil = 0;

    /// <summary>설정의 음성 음량을 현재 재생 경로(Vivox 믹스 또는 왜곡 AudioSource)에 적용한다.</summary>
    public void ApplyVoiceVolume()
    {
        float volume = GameSettings.VoiceVolume;
        if (m_loggedIn)
            VivoxService.Instance.SetOutputDeviceVolume(ToVivoxVolume(volume));

        m_distortion?.ApplyVolume();
    }

    /// <summary>설정값은 두고 음성 출력을 강제로 무음으로 내린다. 복원은 ApplyVoiceVolume.</summary>
    public void ForceMuteOutput()
    {
        if (m_loggedIn)
            VivoxService.Instance.SetOutputDeviceVolume(k_vivoxVolumeMute);
    }

    /// <summary>PTT 송신 차단 — VoiceInputRouter로 그대로 전달한다. 완전 사망 규칙용.</summary>
    public void SetTransmitBlocked(bool blocked) => m_input?.SetTransmitBlocked(blocked);

    private static int ToVivoxVolume(float volume01)
    {
        if (volume01 <= 0f)
            return k_vivoxVolumeMute;

        return Mathf.RoundToInt(Mathf.Lerp(k_vivoxVolumeFloor, k_vivoxVolumeCeil, volume01));
    }

    /// <summary>먹통 음성 왜곡을 켜고 끈다 — <see cref="DeviceBlackoutView"/>가 먹통 플래그에 맞춰 호출한다.</summary>
    public void SetVoiceDistorted(bool distorted) => m_distortion?.SetDistorted(distorted);

    public async UniTask LogoutAsync()
    {
        UnhookParticipantEvents();
        m_speakingByPlayer.Clear();
        m_distortion?.NotifyVoiceEnded();
        m_input?.NotifyVoiceEnded();
        m_positionReporter?.StopReporting();

        if (m_radioJoined || m_proximityJoined)
        {
            await VivoxService.Instance.LeaveAllChannelsAsync();
            m_radioJoined = false;
            m_proximityJoined = false;
        }
        if (m_loggedIn)
        {
            await VivoxService.Instance.LogoutAsync();
            m_loggedIn = false;
        }

        SetVoiceState(EVoiceState.Idle);
    }

    private void HandleSessionJoined(string sessionId)
    {
        JoinChannelAsync(sessionId).Forget();
    }

    private void HandleSessionLeft()
    {
        m_distortion?.NotifyVoiceEnded();
        SetVoiceState(EVoiceState.Idle);
        LeaveChannelAsync().Forget();
    }

    private void HandleConnectionLost(EConnectionLostReason reason)
    {
        m_positionReporter?.StopReporting();
        m_input?.NotifyChannelsLeft();
        CleanupAsync().Forget();
    }

    private void HandleAuthSignedOut()
    {
        CleanupAsync().Forget();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        CleanupAsync().Forget();
    }

    private async UniTaskVoid CleanupAsync()
    {
        try { await LogoutAsync(); }
        catch (TimeoutException)
        {
            Debug.LogWarning("[VivoxManager] 종료 중 채널 이탈 응답 없음 (무해)");
        }
        catch (Exception ex) { Debug.LogError($"[VivoxManager] 정리 실패: {ex}"); }
    }

    [Tooltip("OnGUI 디버그 패널 표시 — 테스트 씬 수동 조작용 (#247)")]
    [SerializeField] private bool m_showDebugGui;

    [SerializeField] private float m_guiTopOffset = 10f;

    private void OnGUI()
    {
        if (!m_showDebugGui) return;
        if (m_session != null && m_session.Auth != null && m_session.Auth.IsNetworkConnected) return;

        GUILayout.BeginArea(new Rect(450, m_guiTopOffset, 320, 240));
        GUILayout.Label("Vivox 무전 — 상태");
        GUILayout.Label($"LoggedIn: {m_loggedIn}");
        GUILayout.Label($"Proximity Joined: {m_proximityJoined}");
        GUILayout.Label($"Radio Joined: {m_radioJoined}");
        GUILayout.Label($"Transmitting(PTT): {m_input != null && m_input.IsTransmitting}");
        GUILayout.Label($"Mic Muted: {GameSettings.MicMuted}");
        GUILayout.Label($"Push To Talk: {PushToTalkBinding}");
        GUILayout.Label($"Mic Mute Toggle: {MicMuteBinding}");
        GUILayout.Space(6);
        GUILayout.Label($"{ToLabel(VoiceState)} ({VoiceState})");
        if (!string.IsNullOrEmpty(m_statusDetail)) GUILayout.Label(m_statusDetail);
        if (m_distortion != null && m_distortion.IsDistorted) GUILayout.Label("음성 왜곡(먹통) 중");
        GUILayout.EndArea();
    }
}
