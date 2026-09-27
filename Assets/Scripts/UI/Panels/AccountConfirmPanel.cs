using System;
using TMPro;
using UnityEngine;

/// <summary>
/// 되돌릴 수 없는 계정 연동·전환 전에 보낼 아이디를 보여 주고 확인받는 창. 확정 시 넘겨받은 콜백을 부른다.
/// </summary>
public class AccountConfirmPanel : ConfirmPanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_messageText;

    private Action m_onConfirm;

    /// <summary>열기 **전에** 문구와 확정 시 실행할 동작을 넘긴다.</summary>
    public void Prepare(string message, Action onConfirm)
    {
        m_onConfirm = onConfirm;
        if (m_messageText != null)
            m_messageText.text = message;
    }

    public override void ClosePanel()
    {
        m_onConfirm = null;
        base.ClosePanel();
    }

    protected override void OnConfirm()
    {
        var confirmed = m_onConfirm;
        ClosePanel();
        confirmed?.Invoke();
    }
}
