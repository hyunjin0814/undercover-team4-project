using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 본부 열람 패널 베이스 — E로 열고 Esc로 닫으며, 여는 동안 입력을 정지하고 커서를 푼다.
/// </summary>
public abstract class HqPanelView : MonoBehaviour
{
    [Header("루트")]
    [SerializeField]
    protected GameObject m_root;

    private PlayerInputHandler m_input;
    private bool m_isOpen;

    protected virtual void Awake()
    {
        if (m_root != null)
            m_root.SetActive(false);
    }

    public void Open(GameObject interactor)
    {
        if (m_isOpen || interactor == null || m_root == null)
            return;

        m_input = interactor.GetComponent<PlayerInputHandler>();

        m_isOpen = true;
        m_root.SetActive(true);
        m_input?.SetSuspended(true);
        CursorLock.PushUnlock();

        OnOpened();
    }

    public void Close()
    {
        if (!m_isOpen)
            return;

        m_isOpen = false;
        if (m_root != null)
            m_root.SetActive(false);

        OnClosed();

        if (m_input != null)
            m_input.SetSuspended(false);
        CursorLock.PopUnlock();

        m_input = null;
    }

    /// <summary>열린 직후 — 데이터 구독·최초 그리기.</summary>
    protected abstract void OnOpened();

    /// <summary>닫히는 중 — 구독 해제.</summary>
    protected virtual void OnClosed() { }

    protected virtual void OnDisable() => Close();

    protected virtual void Update()
    {
        if (!m_isOpen)
            return;

        EscMenuGuard.BlockThisFrame();

        if (m_input == null)
        {
            Close();
            return;
        }
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            Close();
    }
}
