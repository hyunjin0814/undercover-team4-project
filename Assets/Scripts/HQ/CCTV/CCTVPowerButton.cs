using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 콘솔 전원 버튼 — 화면 송출을 끄고 켠다.
/// </summary>
public class CCTVPowerButton : MonoBehaviour, IInteractable
{
    [SerializeField] private CCTVSwitcher m_switcher;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvPower;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor)) return;
        m_switcher.RequestTogglePowerRpc();
    }

    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned 
        && !m_switcher.IsExternallyJammed;
}
