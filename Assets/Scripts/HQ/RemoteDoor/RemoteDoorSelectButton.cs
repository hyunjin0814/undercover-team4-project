using UnityEngine;
using UnityEngine.Localization;

public enum DoorSelectDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// 원격 문 콘솔의 목록 이동 버튼 — 조준한 클라이언트에서 콘솔의 서버 RPC를 호출한다.
/// </summary>
public class RemoteDoorSelectButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    [SerializeField]
    private DoorSelectDirection m_direction = DoorSelectDirection.Next;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.RemoteDoorSelect;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_console.RequestSelectRpc((int)m_direction);
    }

    public bool CanInteract(GameObject interactor) =>
        m_console != null && m_console.IsSpawned && m_console.DoorCount > 1;
}
