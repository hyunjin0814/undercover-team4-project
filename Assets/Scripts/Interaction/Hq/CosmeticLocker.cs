using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점의 치장 락커 — E를 누르면 로비와 같은 커스터마이징 창(PlayerColorPanel)을 연다. 순수 로컬 동작이다.
/// </summary>
public class CosmeticLocker : MonoBehaviour, IInteractable
{
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Cosmetics;

    public bool CanInteract(GameObject interactor) => FindPanel() != null;

    public void Interact(GameObject interactor)
    {
        PlayerColorPanel panel = FindPanel();
        if (panel == null)
        {
            Debug.LogWarning($"[{nameof(CosmeticLocker)}] 이 씬에 커스터마이징 창이 없습니다 (#818)", this);
            return;
        }

        panel.OpenPanel();
    }

    private static PlayerColorPanel FindPanel() =>
        App.UI.Current != null && App.UI.Current.TryGetPanel(out PlayerColorPanel panel) ? panel : null;
}
