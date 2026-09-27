using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 조준한 대상의 키 + 동작 안내("[E] 문 열기")를 표시한다. App.UI.InteractPrompt로 접근한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class InteractPromptView : LocalizedMessageView
{
    [Tooltip("키 + 동작 — HudTable/Hud.Interact.Format. {0}=키 표기, {1}=동작")]
    [SerializeField]
    private LocalizedString m_format;

    [Tooltip("막혔을 때 — HudTable/Hud.Interact.BlockedFormat. {0}=키 표기, {1}=동작, {2}=사유")]
    [SerializeField]
    private LocalizedString m_blockedFormat;

    [Tooltip("막힌 대상의 배경 톤 — 눌러도 지금은 안 된다는 것을 색으로 가른다")]
    [SerializeField]
    private Color m_blockedTone = new Color(0.25f, 0.25f, 0.25f, 0.75f);

    private LocalizedString m_shownAction;
    private LocalizedString m_shownReason;
    private string m_shownKey;

    public bool IsPromptShowing => m_shownAction != null && IsShowing;

    protected override void Awake()
    {
        base.Awake();

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    protected override void OnDestroy()
    {
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
        base.OnDestroy();
    }

    /// <summary>안내를 띄운다 — 지울 때까지 남는다. reason이 있으면 회색 톤 + 사유가 붙는다.</summary>
    public void ShowPrompt(string keyLabel, LocalizedString action, LocalizedString reason)
    {
        if (action == null || action.IsEmpty)
        {
            HidePrompt();
            return;
        }

        if (
            ReferenceEquals(action, m_shownAction)
            && ReferenceEquals(reason, m_shownReason)
            && keyLabel == m_shownKey
        )
        {
            return;
        }

        m_shownAction = action;
        m_shownReason = reason;
        m_shownKey = keyLabel;
        Apply();
    }

    /// <summary>조준이 풀렸다 — 떠 있던 안내를 지운다.</summary>
    public void HidePrompt()
    {
        if (m_shownAction == null)
            return;

        m_shownAction = null;
        m_shownReason = null;
        m_shownKey = null;
        HideImmediate();
    }

    /// <summary>동작·사유를 문자열로 풀어 형식 문구에 넣어 표시한다.</summary>
    private void Apply()
    {
        LocalizedString format = m_shownReason != null ? m_blockedFormat : m_format;
        if (format == null || format.IsEmpty)
            return;

        format.Arguments =
            m_shownReason != null
                ? new object[]
                {
                    m_shownKey,
                    m_shownAction.GetLocalizedString(),
                    m_shownReason.GetLocalizedString(),
                }
                : new object[] { m_shownKey, m_shownAction.GetLocalizedString() };

        ApplyTone(m_shownReason != null ? m_blockedTone : (Color?)null);
        ShowMessage(format);
    }

    private void HandleLocaleChanged(UnityEngine.Localization.Locale locale)
    {
        if (m_shownAction != null)
            Apply();
    }
}
