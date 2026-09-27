using System;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// PTT 무전 송신과 마이크 음소거 토글을 처리하는 VivoxManager 부품.
/// </summary>
public class VoiceInputRouter : MonoBehaviour
{
    [SerializeField]
    private InputActionReference m_pushToTalkAction;

    [Tooltip("마이크 음소거 토글 (#430)")]
    [SerializeField]
    private InputActionReference m_micMuteToggleAction;

    private bool m_loggedIn;
    private bool m_channelsJoined;
    private string m_proximityChannelName;
    private bool m_transmitting;

    private bool m_transmitBlocked;

    public event Action OnMutedTalkAttempt;

    public bool IsTransmitting => m_transmitting;

    public string PushToTalkBinding =>
        m_pushToTalkAction != null
            ? m_pushToTalkAction.action.GetBindingDisplayString()
            : "(미할당)";

    public string MicMuteBinding =>
        m_micMuteToggleAction != null
            ? m_micMuteToggleAction.action.GetBindingDisplayString()
            : "(미할당)";

    /// <summary>Vivox 로그인 완료 — 이 시점부터 입력 장치 뮤트를 걸 수 있다.</summary>
    public void NotifyLoggedIn() => m_loggedIn = true;

    /// <summary>채널 참가 완료 — 이 시점부터 송신 모드를 바꿀 수 있다.</summary>
    public void NotifyChannelsJoined(string proximityChannelName)
    {
        m_proximityChannelName = proximityChannelName;
        m_channelsJoined = true;
    }

    /// <summary>채널 이탈·비자발 드롭 — 송신 상태를 내린다(로그인은 유지).</summary>
    public void NotifyChannelsLeft()
    {
        m_channelsJoined = false;
        m_transmitting = false;
    }

    /// <summary>Vivox 로그아웃 — 입력이 닿을 대상이 사라졌다.</summary>
    public void NotifyVoiceEnded()
    {
        m_loggedIn = false;
        m_channelsJoined = false;
        m_transmitting = false;
    }

    /// <summary>설정의 음소거 값을 입력 장치에 적용한다.</summary>
    public void ApplyMicMute()
    {
        if (!m_loggedIn)
            return;

        if (GameSettings.MicMuted)
            VivoxService.Instance.MuteInputDevice();
        else
            VivoxService.Instance.UnmuteInputDevice();
    }

    /// <summary>PTT 송신을 강제로 막거나 푼다(음소거와 독립).</summary>
    public void SetTransmitBlocked(bool blocked)
    {
        if (m_transmitBlocked == blocked)
            return;

        m_transmitBlocked = blocked;

        if (blocked && m_transmitting)
            SetRadioTransmit(false);
    }

    private void OnEnable()
    {
        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started += OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled += OnPushToTalkCanceled;
            m_pushToTalkAction.action.Enable();
        }

        if (m_micMuteToggleAction != null)
        {
            m_micMuteToggleAction.action.performed += OnMicMuteToggled;
            m_micMuteToggleAction.action.Enable();
        }
    }

    private void OnDisable()
    {
        if (m_pushToTalkAction != null)
        {
            m_pushToTalkAction.action.started -= OnPushToTalkStarted;
            m_pushToTalkAction.action.canceled -= OnPushToTalkCanceled;
            m_pushToTalkAction.action.Disable();
        }

        if (m_micMuteToggleAction != null)
        {
            m_micMuteToggleAction.action.performed -= OnMicMuteToggled;
            m_micMuteToggleAction.action.Disable();
        }
    }

    private static bool IsTypingInUI()
    {
        EventSystem events = EventSystem.current;
        GameObject selected = events != null ? events.currentSelectedGameObject : null;

        return selected != null
            && selected.TryGetComponent(out TMP_InputField input)
            && input.isFocused;
    }

    private void OnPushToTalkStarted(InputAction.CallbackContext ctx)
    {
        if (IsTypingInUI())
            return;

        if (m_transmitBlocked)
            return;

        if (GameSettings.MicMuted)
            OnMutedTalkAttempt?.Invoke();

        SetRadioTransmit(true);
    }

    private void OnPushToTalkCanceled(InputAction.CallbackContext ctx)
    {
        if (m_transmitting)
            SetRadioTransmit(false);
    }

    private void OnMicMuteToggled(InputAction.CallbackContext ctx)
    {
        if (!m_loggedIn)
            return;
        if (IsTypingInUI())
            return;

        GameSettings.MicMuted = !GameSettings.MicMuted;
    }

    private void SetRadioTransmit(bool on)
    {
        if (!m_channelsJoined)
            return;

        m_transmitting = on;

        var mode = on ? TransmissionMode.All : TransmissionMode.Single;
        string channel = on ? null : m_proximityChannelName;
        VivoxService.Instance.SetChannelTransmissionModeAsync(mode, channel).AsUniTask().Forget();
    }
}
