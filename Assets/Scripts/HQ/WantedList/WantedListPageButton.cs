using UnityEngine;
using UnityEngine.Localization;

public enum WantedPageDirection
{
    Prev = -1,
    Next = 1,
}

/// <summary>
/// 수배 리스트 페이지 넘김 버튼(로컬 표시 전환).
/// </summary>
public class WantedListPageButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private WantedListView m_view;

    [SerializeField]
    private WantedPageDirection m_direction = WantedPageDirection.Next;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.WantedPage;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_view.ChangePage((int)m_direction);
    }

    public bool CanInteract(GameObject interactor) => m_view != null && m_view.HasMultiplePages;
}
