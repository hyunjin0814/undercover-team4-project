using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 세력 문양 대조 게시판 — 상호작용(E)하면 이번 세션의 진짜 문양 목록을 펼친다. (#222)
/// 인명부 책 옆에 놓이는 설치형 고정 오브젝트. 표시는 FactionSymbolBoardView가 맡는다.
/// </summary>
public class FactionSymbolBoard : MonoBehaviour, IInteractable
{
    [SerializeField]
    private FactionSymbolBoardView m_view;

    public bool CanInteract(GameObject interactor) => m_view != null;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.FactionSymbol;

    public void Interact(GameObject interactor) => m_view.Open(interactor);
}
