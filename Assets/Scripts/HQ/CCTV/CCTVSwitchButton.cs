using UnityEngine;
using UnityEngine.Localization;

public enum CCTVSwitchDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// CCTV 채널 전환 버튼 — 콘솔 화면과 분리된 설치물. (#362)
/// 순수 MonoBehaviour다: Interact()는 조준한 클라이언트에서 실행되므로(PlayerInteractor 오너 전용)
/// 여기서 스위처의 서버 RPC를 호출하면 되고, 버튼 자체엔 NetworkObject가 필요 없다.
/// </summary>
public class CCTVSwitchButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    [SerializeField]
    private CCTVSwitchDirection m_direction = CCTVSwitchDirection.Next;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvSwitch;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestSwitchRpc((int)m_direction);
    }

    // 윤곽선 판정과 실제 동작 조건을 일치시킨다 — RequestSwitchRpc의 서버 가드와 같은 기준
    // (전원 OFF·먹통 중에는 눌러도 채널이 바뀌지 않으므로 윤곽선도 뜨지 않아야 한다)
    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.ChannelCount > 1
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
