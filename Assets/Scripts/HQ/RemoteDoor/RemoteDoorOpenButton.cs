using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 원격 문 개방 버튼 — 모니터에서 선택된 문을 여닫는다(잠긴 문 포함).
/// </summary>
public class RemoteDoorOpenButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RemoteDoorOpen;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_console.RequestToggleSelectedRpc();
    }

    public bool CanInteract(GameObject interactor) =>
        m_console != null && m_console.IsSpawned && m_console.SelectedDoor != null;
}
