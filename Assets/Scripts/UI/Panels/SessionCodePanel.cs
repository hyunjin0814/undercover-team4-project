using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 세션 코드 HUD — 인게임 우측 상단에 현재 세션 코드만 표시한다. (#247)
/// 참가자에게 코드를 공유할 수 있게 상시 노출하고, 세션이 없으면(오프라인·테스트) 아무것도 그리지 않는다.
/// OnGUI 디버그 UI를 대체하는 정식 표시.
///
/// <b>복사 경로가 둘이다 — 버튼과 단축키</b> (#986). 상점·인게임에서는 플레이어가 스폰돼
/// 커서가 잠기므로(CursorLock) 버튼을 누를 수 없다. 커서를 쓰지 않는 단축키가 있어야
/// 로비·상점·인게임에서 같은 방법으로 코드를 넘길 수 있다.
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

    // UI 맵에 둔다 — 플레이어 입력(Player 맵)은 스폰돼 있어야 살아 있어서 로비에서 안 먹는다 (#986)
    [Tooltip("커서 없이 복사하는 단축키 — UI/CopySessionCode")]
    [SerializeField]
    private InputActionReference m_copyAction;

    // 코드가 대입하는 자리라 라벨에 LocalizeStringEvent를 붙일 수 없다 — 서로 덮어쓴다. (#497)
    // CommonTable에 두는 이유는 이 패널이 Lobby·Shop 두 씬에 걸쳐 있어서다 (문서 §3).
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

    // 문구를 내릴 시각(Time.time). 0이면 지금 떠 있지 않다.
    private float m_copiedHideTime;

    private SessionManager Session => App.Net.Session;

    private bool m_bound;
    private bool m_hintBound;

    // 지금 라벨에 쓸 안내 문구 — 언어가 바뀌면 StringChanged가 갱신한다. 비어 있으면 라벨을 끈다.
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

        // 단축키는 이 패널이 직접 켜고 끈다 — 커서가 잠겨 있어도 눌리는 유일한 경로다 (#986)
        if (m_copyAction != null && m_copyAction.action != null)
        {
            m_copyAction.action.performed += HandleCopyPerformed;
            m_copyAction.action.Enable();
        }

        // 껐다 켜면 이전 확인 문구는 지운다 — 남겨두면 누른 적 없는 문구가 떠 있는 채로 열린다
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

        Unbind(); // 꺼진 HUD가 언어 변경에 반응하지 않게 — 다시 켜질 때 OnEnable이 건다
        UnbindHint();
    }

    /// <summary>
    /// 코드를 클립보드에 복사하고 <b>코드 바로 아래</b>에 확인 문구를 띄운다. 세션이 없으면 아무 것도 하지 않는다.
    ///
    /// 전역 토스트(<c>App.UI.Toast</c>)를 쓰지 않는 이유는 자리다 — 코드는 우측 상단인데 확인 문구가
    /// 화면 반대편에 뜨면 방금 누른 것의 결과로 읽히지 않는다. (#977)
    /// </summary>
    private void HandleCopyClicked()
    {
        if (Session == null || Session.CurrentSession == null)
            return;

        GUIUtility.systemCopyBuffer = Session.CurrentSession.Code;
        ShowCopied();
    }

    // 단축키 — 커서 잠금과 무관하게 눌린다 (#986). 버튼과 완전히 같은 일을 한다.
    private void HandleCopyPerformed(InputAction.CallbackContext context) => HandleCopyClicked();

    private void ShowCopied()
    {
        if (m_copiedLabel == null)
        {
            Debug.LogWarning("SessionCodePanel: 복사 확인 라벨이 연결되지 않았습니다.", this);
            return;
        }

        // 한 번 읽어 대입한다 — 문구가 2초만 떠 있어 그사이 언어가 바뀌는 경우를 다룰 이유가 없다
        // (세션 코드 라벨이 StringChanged를 구독하는 것과 갈리는 지점).
        m_copiedLabel.text = m_copiedToast != null && !m_copiedToast.IsEmpty
            ? m_copiedToast.GetLocalizedString()
            : string.Empty;

        m_copiedLabel.enabled = true;
        m_copiedHideTime = Time.time + m_copiedSeconds;
    }

    // 라벨은 자리 하나를 둘이 나눠 쓴다 — 평소엔 단축키 안내, 누른 직후 2초는 복사 확인.
    private void ApplyLabel()
    {
        if (m_copiedLabel == null)
            return;

        if (m_copiedHideTime > 0f)
            return; // 확인 문구가 떠 있는 동안은 건드리지 않는다 — 그 뒤 Update가 안내로 되돌린다

        m_copiedLabel.text = m_hint;
        m_copiedLabel.enabled = !string.IsNullOrEmpty(m_hint);
    }

    // 코루틴 대신 타이머로 내린다 — architecture.md 비동기 대기 규칙(코루틴 금지).
    private void Update()
    {
        if (m_copiedHideTime <= 0f)
            return;

        if (Time.time < m_copiedHideTime)
            return;

        m_copiedHideTime = 0f;
        ApplyLabel(); // 확인 문구를 내리고 단축키 안내로 되돌린다
    }

    private void HandleSessionJoined(string sessionId) => Refresh();

    /// <summary>
    /// 세션이 있으면 코드를 보이고, 없으면(오프라인·테스트) 아무것도 그리지 않는다.
    /// 한 번 읽어 대입하지 않고 구독하는 이유는 로비에서 설정 창을 열어 언어를 바꿀 수 있기 때문이다 (#374).
    /// </summary>
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

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 코드가 들어간 문장이 나온다
        m_codeFormat.Arguments = new object[] { Session.CurrentSession.Code };
        m_codeFormat.StringChanged += HandleCodeChanged;
        m_bound = true;

        BindHint();
    }

    /// <summary>
    /// 단축키 안내를 라벨에 건다 — 키 표기는 하드코딩하지 않고 바인딩에서 읽는다 (#664와 같은 방침).
    /// 코드 문구와 마찬가지로 구독해 두는 이유는 로비에서 언어를 바꿀 수 있어서다 (#374).
    /// </summary>
    private void BindHint()
    {
        UnbindHint();

        if (m_copyAction == null || m_copyAction.action == null)
            return; // 단축키가 안 물렸다 — 버튼만 남는다(로비에서는 그걸로 충분하다)

        if (m_copyHint == null || m_copyHint.IsEmpty)
            return;

        // 바인딩 하나짜리 액션이라 0번을 쓴다 — 여럿으로 늘리면 PlayerInputHandler.BindingDisplay처럼 골라야 한다
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
