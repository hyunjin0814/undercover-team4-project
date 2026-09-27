using UnityEngine;
using UnityEngine.Localization;

public enum CCTVSwitchDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// CCTV 채널 전환 버튼 — 조준한 클라이언트에서 스위처의 서버 RPC를 호출한다.
/// </summary>
public class CCTVSwitchButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    [SerializeField]
    private CCTVSwitchDirection m_direction = CCTVSwitchDirection.Next;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvSwitch;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestSwitchRpc((int)m_direction);
    }

    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.ChannelCount > 1
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
