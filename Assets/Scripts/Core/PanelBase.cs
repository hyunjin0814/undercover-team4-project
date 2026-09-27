using UnityEngine;

/// <summary>
/// UI 패널 베이스 — Awake에서 현재 씬의 UI 매니저에 스스로 등록된다.
/// 열고 닫기는 App.UI.Current.OpenPanel 경유로만 한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIPanel)]
public abstract class PanelBase : MonoBehaviour
{
    [Tooltip("비우면 자기 GameObject를 패널 루트로 사용")]
    [SerializeField]
    protected GameObject m_panelRoot;

    [Tooltip("패널과 함께 켜고 끄는 딤 배경 (쓰지 않으면 비워 둔다)")]
    [SerializeField]
    protected GameObject m_background;

    public bool IsOpened => m_panelRoot != null && m_panelRoot.activeSelf;

    public abstract bool CanCloseWithESC { get; }

    public abstract bool IsStackable { get; }

    public virtual bool IsEscMenu => false;

    public virtual bool CanOpenFromEsc => true;

    protected virtual bool OpenOnAwake => false;

    private bool m_blocked;

    protected virtual PlayerInputHandler BlockTarget => null;

    protected void SetBlocked(bool blocked)
    {
        if (m_blocked == blocked)
            return;

        m_blocked = blocked;

        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        PlayerInputHandler input = BlockTarget;
        if (input != null)
            input.SetSuspended(blocked);
    }

    protected virtual void Awake()
    {
        if (m_panelRoot == null)
            m_panelRoot = gameObject;

        m_panelRoot.SetActive(OpenOnAwake);
        SetBackgroundActive(OpenOnAwake);

        if (App.UI.Current == null)
        {
            Debug.LogError(
                $"[{GetType().Name}] 씬에 UI 매니저가 없어 패널을 등록하지 못했습니다.",
                this
            );
            return;
        }

        App.UI.Current.RegisterPanel(this);
    }

    protected virtual void OnDestroy()
    {
        if (App.UI.Current != null)
            App.UI.Current.UnregisterPanel(this);
    }

    public virtual void OpenPanel()
    {
        if (IsStackable && !IsOpened && App.UI.Current != null)
            App.UI.Current.PushUIStack(this);

        SetBackgroundActive(true);
        m_panelRoot.SetActive(true);
    }

    public virtual void ClosePanel()
    {
        if (IsStackable && IsOpened && App.UI.Current != null)
            App.UI.Current.PopUIStack(this);

        SetBackgroundActive(false);
        m_panelRoot.SetActive(false);
    }

    private void SetBackgroundActive(bool active)
    {
        if (m_background != null)
            m_background.SetActive(active);
    }
}
