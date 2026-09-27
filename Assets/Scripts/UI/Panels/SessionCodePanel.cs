using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 인게임 우측 상단에 세션 코드를 표시하고, 버튼·단축키로 클립보드에 복사한다.
/// </summary>
public class SessionCodePanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;
    protected override bool OpenOnAwake => true;

    [Header("UI 참조")]
    [SerializeField]
    private TMP_Text m_codeText;

    [Tooltip("누르면 세션 코드를 클립보드에 복사한다")]
    [SerializeField]
    private Button m_copyButton;

    [Tooltip("커서 없이 복사하는 단축키 — UI/CopySessionCode")]
    [SerializeField]
    private InputActionReference m_copyAction;

    [Tooltip("세션 코드 표시 — Common.Session.Code ({0}=참가 코드)")]
    [SerializeField]
    private LocalizedString m_codeFormat;

    [Tooltip("복사 확인 문구 — Common.Toast.Copied")]
    [SerializeField]
    private LocalizedString m_copiedToast;

    [Tooltip("단축키 안내 — Common.Session.CopyHint ({0}=키 표기)")]
    [SerializeField]
    private LocalizedString m_copyHint;

    [Tooltip("복사 확인 문구를 띄울 라벨 — 세션 코드 바로 아래 자리 (#977)")]
    [SerializeField]
    private TMP_Text m_copiedLabel;

    [Tooltip("복사 확인 문구가 떠 있는 시간(초)")]
    [Min(0.5f)]
    [SerializeField]
    private float m_copiedSeconds = 2f;

    private float m_copiedHideTime;

    private SessionManager Session => App.Net.Session;

    private bool m_bound;
    private bool m_hintBound;

    private string m_hint = string.Empty;

    private void OnEnable()
    {
        if (Session != null)
        {
            Session.OnSessionJoined += HandleSessionJoined;
            Session.OnSessionLeft += Refresh;
        }

        if (m_copyButton != null)
            m_copyButton.onClick.AddListener(HandleCopyClicked);

        if (m_copyAction != null && m_copyAction.action != null)
        {
            m_copyAction.action.performed += HandleCopyPerformed;
            m_copyAction.action.Enable();
        }

        m_copiedHideTime = 0f;

        Refresh();
    }

    private void OnDisable()
    {
        if (Session != null)
        {
            Session.OnSessionJoined -= HandleSessionJoined;
            Session.OnSessionLeft -= Refresh;
        }

        if (m_copyButton != null)
            m_copyButton.onClick.RemoveListener(HandleCopyClicked);

        if (m_copyAction != null && m_copyAction.action != null)
        {
            m_copyAction.action.performed -= HandleCopyPerformed;
            m_copyAction.action.Disable();
        }

        Unbind();
        UnbindHint();
    }

    /// <summary>코드를 클립보드에 복사하고 코드 바로 아래에 확인 문구를 띄운다.</summary>
    private void HandleCopyClicked()
    {
        if (Session == null || Session.CurrentSession == null)
            return;

        GUIUtility.systemCopyBuffer = Session.CurrentSession.Code;
        ShowCopied();
    }

    private void HandleCopyPerformed(InputAction.CallbackContext context) => HandleCopyClicked();

    private void ShowCopied()
    {
        if (m_copiedLabel == null)
        {
            Debug.LogWarning("SessionCodePanel: 복사 확인 라벨이 연결되지 않았습니다.", this);
            return;
        }

        m_copiedLabel.text = m_copiedToast != null && !m_copiedToast.IsEmpty
            ? m_copiedToast.GetLocalizedString()
            : string.Empty;

        m_copiedLabel.enabled = true;
        m_copiedHideTime = Time.time + m_copiedSeconds;
    }

    private void ApplyLabel()
    {
        if (m_copiedLabel == null)
            return;

        if (m_copiedHideTime > 0f)
            return;

        m_copiedLabel.text = m_hint;
        m_copiedLabel.enabled = !string.IsNullOrEmpty(m_hint);
    }

    private void Update()
    {
        if (m_copiedHideTime <= 0f)
            return;

        if (Time.time < m_copiedHideTime)
            return;

        m_copiedHideTime = 0f;
        ApplyLabel();
    }

    private void HandleSessionJoined(string sessionId) => Refresh();

    /// <summary>세션이 있으면 코드를 표시하고, 없으면 아무것도 그리지 않는다.</summary>
    private void Refresh()
    {
        if (m_codeText == null)
            return;

        if (Session == null || Session.CurrentSession == null)
        {
            Unbind();
            UnbindHint();
            m_codeText.text = string.Empty;
            m_hint = string.Empty;
            ApplyLabel();
            return;
        }

        if (m_codeFormat == null || m_codeFormat.IsEmpty)
        {
            Debug.LogWarning("SessionCodePanel: 세션 코드 문구가 연결되지 않았습니다.", this);
            return;
        }

        Unbind();

        m_codeFormat.Arguments = new object[] { Session.CurrentSession.Code };
        m_codeFormat.StringChanged += HandleCodeChanged;
        m_bound = true;

        BindHint();
    }

    /// <summary>바인딩에서 읽은 키 표기로 단축키 안내 라벨을 건다.</summary>
    private void BindHint()
    {
        UnbindHint();

        if (m_copyAction == null || m_copyAction.action == null)
            return;

        if (m_copyHint == null || m_copyHint.IsEmpty)
            return;

        m_copyHint.Arguments = new object[] { m_copyAction.action.GetBindingDisplayString(0) };
        m_copyHint.StringChanged += HandleHintChanged;
        m_hintBound = true;
    }

    private void HandleHintChanged(string localized)
    {
        m_hint = localized;
        ApplyLabel();
    }

    private void UnbindHint()
    {
        if (!m_hintBound)
            return;

        m_copyHint.StringChanged -= HandleHintChanged;
        m_hintBound = false;
    }

    private void HandleCodeChanged(string localized)
    {
        if (m_codeText != null)
            m_codeText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_codeFormat.StringChanged -= HandleCodeChanged;
        m_bound = false;
    }
}
