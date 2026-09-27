using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 신호 해석기 — 본부에 설치되는 단말. 타이핑한 짧은 메시지를 팀 전원에게 보낸다. (GDD 8-3/8-4/4-4, #108)
/// </summary>
public class SignalDecoder : InstallableItem
{
    public const int k_maxMessageLength = 20;

    [Header("수신 표시")]
    [Tooltip("받은 메시지를 화면에 유지하는 시간(초)")]
    [SerializeField]
    private float m_displaySeconds = 8f;

    [Tooltip("수신 문구 형식 — WorldTable/World.SignalDecoder.Received ({0}에 받은 메시지가 들어간다)")]
    [SerializeField]
    private LocalizedString m_receivedFormat;

    /// <summary>조준 안내 문구를 돌려준다.</summary>
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Decoder;

    protected override void OnInteract(GameObject interactor)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SignalInputPanel panel))
            panel.Open(this, interactor);
        else
            Debug.LogWarning("신호 해석기: 입력창 패널을 찾지 못해 열 수 없다", this);
    }

    /// <summary>입력창이 확정한 메시지를 팀 전원에게 보낸다. 보낸 사람의 클라이언트에서 호출된다.</summary>
    public void SendSignal(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (!IsSpawned || IsServer)
        {
            ServerBroadcast(message);
            return;
        }

        RequestBroadcastRpc(message);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestBroadcastRpc(string message)
    {
        ServerBroadcast(message);
    }

    private void ServerBroadcast(string message)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsInstalled)
            return;

        string trimmed = message.Trim();
        if (trimmed.Length == 0)
            return;

        if (trimmed.Length > k_maxMessageLength)
            trimmed = trimmed.Substring(0, k_maxMessageLength);

        if (!IsSpawned)
        {
            ShowLocal(trimmed);
            return;
        }

        BroadcastRpc(trimmed);
    }

    [Rpc(SendTo.Everyone)]
    private void BroadcastRpc(string message) => ShowLocal(message);

    private void ShowLocal(string message)
    {
        Debug.Log($"[신호 해석기] {message}");

        m_receivedFormat.Arguments = new object[] { message };
        App.UI.SignalMessage?.Show(m_receivedFormat, m_displaySeconds);
    }
}
