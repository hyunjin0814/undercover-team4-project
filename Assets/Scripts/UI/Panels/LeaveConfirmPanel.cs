using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 세션 이탈 확인창 — 확정 시 SessionFlow.LeaveToMainAsync를 부른다. ESC로 취소된다.
/// </summary>
public class LeaveConfirmPanel : ConfirmPanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_messageText;

    [Tooltip("호스트 — Common.LeaveConfirm.MessageHost")]
    [SerializeField]
    private LocalizedString m_messageHost;

    [Tooltip("클라이언트 — Common.LeaveConfirm.MessageClient")]
    [SerializeField]
    private LocalizedString m_messageClient;

    private LocalizedString m_boundMessage;

    private static bool IsServer =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    protected override void OnDestroy()
    {
        UnbindMessage();
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        BindMessage(IsServer ? m_messageHost : m_messageClient);
        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        UnbindMessage();
        base.ClosePanel();
    }

    protected override void OnConfirm() => SessionFlow.LeaveToMainAsync().Forget();

    /// <summary>표시 문구를 교체하고 언어 변경을 구독한다.</summary>
    private void BindMessage(LocalizedString message)
    {
        if (m_messageText == null)
            return;

        if (message == null || message.IsEmpty)
        {
            Debug.LogWarning($"[{nameof(LeaveConfirmPanel)}] 이탈 확인 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindMessage();

        m_boundMessage = message;
        m_boundMessage.StringChanged += HandleMessageChanged;
    }

    private void HandleMessageChanged(string localized)
    {
        if (m_messageText != null)
            m_messageText.text = localized;
    }

    private void UnbindMessage()
    {
        if (m_boundMessage == null)
            return;

        m_boundMessage.StringChanged -= HandleMessageChanged;
        m_boundMessage = null;
    }
}
