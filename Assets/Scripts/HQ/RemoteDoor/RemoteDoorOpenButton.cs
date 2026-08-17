using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 원격 문 개방 버튼 — 모니터에서 선택된 문을 여닫는다. (#489)
/// 잠긴 문도 열린다: 잠금은 "현장이 못 연다"는 뜻이고, 그 문을 열어 주는 것이 본부의 역할이다.
/// (<see cref="CCTVPowerButton"/>과 같은 구조, #362)
/// </summary>
public class RemoteDoorOpenButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RemoteDoorOpen;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_console.RequestToggleSelectedRpc();
    }

    // 선택된 문이 없으면(목록이 비었거나 미배선) 눌러도 무동작이므로 윤곽선도 뜨지 않아야 한다
    public bool CanInteract(GameObject interactor) =>
        m_console != null && m_console.IsSpawned && m_console.SelectedDoor != null;
}
