using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 시민 인명부 책 — 상호작용(E)하면 인명부 패널을 펼친다. (#223)
/// 데이터/표시는 DirectoryManager·CitizenDirectoryView가 맡고, 이 컴포넌트는 상호작용 진입점만 담당한다.
/// 설치형 고정 오브젝트 — 상호작용은 오너 클라에서만 일어나 UI는 로컬로 열린다.
/// </summary>
public class CitizenDirectory : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CitizenDirectoryView m_view;

    public bool CanInteract(GameObject interactor) => m_view != null;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Directory;

    public void Interact(GameObject interactor) => m_view.Open(interactor);
}
