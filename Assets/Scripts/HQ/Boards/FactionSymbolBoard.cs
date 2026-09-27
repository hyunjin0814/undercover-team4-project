using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 세력 문양 대조 게시판 — E로 이번 세션의 진짜 문양 목록을 펼친다.
/// </summary>
public class FactionSymbolBoard : MonoBehaviour, IInteractable
{
    [SerializeField]
    private FactionSymbolBoardView m_view;

    public bool CanInteract(GameObject interactor) => m_view != null;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.FactionSymbol;

    public void Interact(GameObject interactor) => m_view.Open(interactor);
}
