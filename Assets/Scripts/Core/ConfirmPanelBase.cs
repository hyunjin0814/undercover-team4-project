using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 예/아니오 확인창 베이스 — 버튼 배선과 연타 방어를 공통으로 처리한다.
/// 파생 클래스는 확정 시 동작(OnConfirm)만 구현한다.
/// </summary>
public abstract class ConfirmPanelBase : PanelBase
{
    [Header("버튼")]
    [SerializeField]
    protected Button m_confirmButton;

    [SerializeField]
    protected Button m_cancelButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(HandleConfirmClicked);
        if (m_cancelButton != null)
            m_cancelButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(HandleConfirmClicked);
        if (m_cancelButton != null)
            m_cancelButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        if (m_confirmButton != null)
            m_confirmButton.interactable = true;

        base.OpenPanel();
    }

    private void HandleConfirmClicked()
    {
        if (m_confirmButton != null)
            m_confirmButton.interactable = false;

        OnConfirm();
    }

    /// <summary>'예'를 눌렀을 때 할 일. 창을 닫을지는 파생이 정한다 — 씬이 넘어가는 창은 닫을 필요가 없다.</summary>
    protected abstract void OnConfirm();
}
