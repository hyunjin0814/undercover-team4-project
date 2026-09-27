using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tab 홀드로 팀 상황판을 여닫는 오너 로컬 입력 처리.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerTeamStatusInput : NetworkBehaviour
{
    private PlayerInputHandler m_inputHandler;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_inputHandler.OnTeamStatusOpened += Open;
        m_inputHandler.OnTeamStatusClosed += Close;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner || m_inputHandler == null)
            return;

        m_inputHandler.OnTeamStatusOpened -= Open;
        m_inputHandler.OnTeamStatusClosed -= Close;

        Close();
    }

    private void OnDisable() => Close();

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            Close();
    }

    private void Open()
    {
        if (App.UI.Current != null)
            App.UI.Current.OpenPanel<TeamStatusPanel>();

        m_inputHandler.SetTeamStatusPeeking(true);
    }

    private void Close()
    {
        if (m_inputHandler == null)
            return;

        if (App.UI.Current != null)
            App.UI.Current.ClosePanel<TeamStatusPanel>();

        m_inputHandler.SetTeamStatusPeeking(false);
    }
}
