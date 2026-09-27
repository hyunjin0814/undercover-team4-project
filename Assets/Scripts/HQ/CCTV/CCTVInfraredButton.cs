using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 적외선(야시경) 토글 버튼 — 전원/채널 버튼과 같은 방식.
/// </summary>
public class CCTVInfraredButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvInfrared;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestToggleInfraredRpc();
    }

    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
