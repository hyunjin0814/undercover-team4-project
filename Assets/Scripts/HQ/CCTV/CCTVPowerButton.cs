using UnityEngine;
using UnityEngine.Localization;

/// <summary>CCTV 콘솔 전원 버튼 — 화면 송출을 끄고 켠다. (#362)</summary>
public class CCTVPowerButton : MonoBehaviour, IInteractable
{
    [SerializeField] private CCTVSwitcher m_switcher;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvPower;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor)) return;
        m_switcher.RequestTogglePowerRpc();
    }

    // 먹통 중엔 전원 버튼도 먹지 않는다 — 플레이어가 이벤트를 무력화하지 못하게 (#106)
    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned 
        && !m_switcher.IsExternallyJammed;
}
