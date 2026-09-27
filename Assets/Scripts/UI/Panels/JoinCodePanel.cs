using System;
using TMPro;
using UnityEngine;

/// <summary>
/// 세션 코드 입력 모달 — 입력한 코드를 콜백으로 넘기며, 실제 참가는 SessionPanel이 한다.
/// </summary>
public class JoinCodePanel : ConfirmPanelBase
{
    [Header("입력")]
    [SerializeField]
    private TMP_InputField m_codeInput;

    private Action<string> m_onConfirm;

    public void Prepare(Action<string> onConfirm) => m_onConfirm = onConfirm;

    public override void OpenPanel()
    {
        if (m_codeInput != null)
            m_codeInput.SetTextWithoutNotify(string.Empty);

        base.OpenPanel();

        if (m_codeInput != null)
            m_codeInput.ActivateInputField();
    }

    public override void ClosePanel()
    {
        m_onConfirm = null;

        if (m_codeInput != null)
            m_codeInput.DeactivateInputField();

        base.ClosePanel();
    }

    protected override void OnConfirm()
    {
        Action<string> confirmed = m_onConfirm;
        string code = m_codeInput != null ? m_codeInput.text.Trim() : string.Empty;

        ClosePanel();
        confirmed?.Invoke(code);
    }
}
