using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ESC 일시정지 패널 — 나가기(확인창 경유)와 설정을 제공한다.
/// 열려 있는 동안 플레이어 입력을 정지하고 커서를 푼다.
/// </summary>
public class PausePanel : PanelBase
{
    [Header("버튼")]
    [SerializeField]
    private Button m_resumeButton;

    [SerializeField]
    private Button m_leaveButton;

    [SerializeField]
    private Button m_settingsButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;
    public override bool IsEscMenu => true;

    public override bool CanOpenFromEsc => !EscMenuGuard.IsBlocked;

    private bool m_playerBlocked;

    protected override void Awake()
    {
        base.Awake();
        if (m_resumeButton != null)
            m_resumeButton.onClick.AddListener(ClosePanel);
        if (m_leaveButton != null)
            m_leaveButton.onClick.AddListener(HandleLeave);
        if (m_settingsButton != null)
            m_settingsButton.onClick.AddListener(OpenSettings);
    }

    protected override void OnDestroy()
    {
        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_resumeButton != null)
            m_resumeButton.onClick.RemoveListener(ClosePanel);
        if (m_leaveButton != null)
            m_leaveButton.onClick.RemoveListener(HandleLeave);
        if (m_settingsButton != null)
            m_settingsButton.onClick.RemoveListener(OpenSettings);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SetLocalPlayerBlocked(true);
    }

    public override void ClosePanel()
    {
        SetLocalPlayerBlocked(false);
        base.ClosePanel();
    }

    private static void OpenSettings() => App.UI.Current?.OpenPanel<SettingsPanel>();

    private static void HandleLeave() => App.UI.Current?.OpenPanel<LeaveConfirmPanel>();

    private void SetLocalPlayerBlocked(bool blocked)
    {
        if (m_playerBlocked == blocked)
            return;

        m_playerBlocked = blocked;

        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        GameObject player = LocalPlayer;
        if (player == null)
            return;

        PlayerInputHandler input = player.GetComponent<PlayerInputHandler>();
        if (input != null)
            input.SetSuspended(blocked);
    }

    private static GameObject LocalPlayer
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
                return null;
            return nm.LocalClient.PlayerObject.gameObject;
        }
    }
}
