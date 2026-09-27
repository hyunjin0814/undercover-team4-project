using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 시민 인명부 책 — E로 인명부 패널을 펼치는 상호작용 진입점.
/// </summary>
public class CitizenDirectory : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CitizenDirectoryView m_view;

    public bool CanInteract(GameObject interactor) => m_view != null;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Directory;

    public void Interact(GameObject interactor) => m_view.Open(interactor);
}
