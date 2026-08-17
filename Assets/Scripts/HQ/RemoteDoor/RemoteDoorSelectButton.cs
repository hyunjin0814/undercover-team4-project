using UnityEngine;
using UnityEngine.Localization;

public enum DoorSelectDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// 본부 원격 문 개방 콘솔의 목록 이동 버튼 — 모니터와 분리된 설치물. (#489)
/// 순수 MonoBehaviour다: Interact()는 조준한 클라이언트에서 실행되므로(PlayerInteractor 오너 전용)
/// 여기서 콘솔의 서버 RPC를 호출하면 되고, 버튼 자체엔 NetworkObject가 필요 없다.
/// (<see cref="CCTVSwitchButton"/>과 같은 구조, #362)
/// </summary>
public class RemoteDoorSelectButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    [SerializeField]
    private DoorSelectDirection m_direction = DoorSelectDirection.Next;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RemoteDoorSelect;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_console.RequestSelectRpc((int)m_direction);
    }

    // 윤곽선 판정과 실제 동작 조건을 일치시킨다 — 문이 하나뿐이면 눌러도 바뀌는 것이 없으므로 뜨지 않는다
    public bool CanInteract(GameObject interactor) =>
        m_console != null && m_console.IsSpawned && m_console.DoorCount > 1;
}
