using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// CCTV 콘솔 키패드의 숫자 버튼 — 누른 숫자를 채널 번호 입력에 쌓는다.
/// </summary>
public class CCTVKeypadButton : MonoBehaviour, IInteractable
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    [Tooltip("이 버튼의 숫자")]
    [Range(0, 9)]
    [SerializeField]
    private int m_digit;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.CctvKeypad;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;
        m_switcher.RequestAppendDigitRpc(m_digit);
    }

    public bool CanInteract(GameObject interactor) =>
        m_switcher != null
        && m_switcher.IsSpawned
        && m_switcher.IsPowered
        && !m_switcher.IsExternallyJammed;
}
