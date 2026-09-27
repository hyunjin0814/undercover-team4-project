using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 설정 창에서 여는 개발진 명단 창(ESC 스택 패널).
/// </summary>
public class CreditsPanel : PanelBase
{
    [Header("문구")]
    [SerializeField]
    private TMP_Text m_rosterText;

    [Tooltip("개발진 명단 — Settings.Credits.Roster (줄바꿈으로 한 사람씩)")]
    [SerializeField]
    private LocalizedString m_roster;

    [Header("버튼")]
    [SerializeField]
    private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    protected override void OnDestroy()
    {
        if (m_closeButton != null)
            m_closeButton.onClick.RemoveListener(ClosePanel);

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        base.OnDestroy();
    }

    public override void OpenPanel()
    {
        RefreshRoster();
        base.OpenPanel();
    }

    private void HandleLocaleChanged(Locale locale) => RefreshRoster();

    private void RefreshRoster()
    {
        if (m_rosterText == null || m_roster == null || m_roster.IsEmpty)
            return;

        m_rosterText.text = m_roster.GetLocalizedString();
    }
}
