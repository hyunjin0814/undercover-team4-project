using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Title 씬 UI 매니저 — 첫 화면(로그인 관문 / 세션 화면)을 고른다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIManagement)]
public class TitleUIManager : UIManagerBase
{
    [SerializeField]
    private Button m_quitBtn;

    [SerializeField]
    private Button m_settingsBtn;

    [Tooltip("튜토리얼 — 로그인 없이도 눌린다 (#663). 배선하지 않으면 버튼이 없는 것으로 취급한다")]
    [SerializeField]
    private Button m_tutorialBtn;

    /// <summary>관문 통과 여부에 따라 첫 화면(관문 또는 세션 화면)을 연다.</summary>
    private void Start()
    {
        AuthBootstrap auth = App.Net.Auth;

        if (auth != null)
            auth.OnSignedOut += ReturnToAuthGate;

        bool remembered = auth != null && (auth.HasPassedAuthGate || auth.RememberedAuthGate);
        bool canRestoreSession =
            auth != null && (auth.HasPassedAuthGate || auth.IsSignedIn || auth.IsSigningIn);

        if (remembered && canRestoreSession)
        {
            if (TryGetPanel(out SessionPanel session))
                session.OpenWithBackdropFade();
            else
                OpenPanel<SessionPanel>();

            if (!auth.HasPassedAuthGate && !auth.IsSignedIn && auth.IsSigningIn)
                auth.OnSigningInChanged += HandleRestoreSigningChanged;
        }
        else
        {
            OpenPanel<AuthGatePanel>();
        }
    }

    /// <summary>관문을 건너뛴 뒤 자동 익명 로그인이 실패하면 관문으로 되돌린다.</summary>
    private void HandleRestoreSigningChanged()
    {
        AuthBootstrap auth = App.Net.Auth;
        if (auth == null || auth.IsSigningIn)
            return;

        auth.OnSigningInChanged -= HandleRestoreSigningChanged;

        if (!auth.IsSignedIn)
            ReturnToAuthGate();
    }

    protected override void OnDestroy()
    {
        if (App.Net.Auth != null)
        {
            App.Net.Auth.OnSignedOut -= ReturnToAuthGate;
            App.Net.Auth.OnSigningInChanged -= HandleRestoreSigningChanged;
        }

        base.OnDestroy();
    }

    private void OnEnable()
    {
        m_quitBtn.onClick.AddListener(QuitGame);
        m_settingsBtn.onClick.AddListener(OpenSettings);

        if (m_tutorialBtn != null)
            m_tutorialBtn.onClick.AddListener(TutorialFlow.Enter);
    }

    private void OnDisable()
    {
        m_quitBtn.onClick.RemoveListener(QuitGame);
        m_settingsBtn.onClick.RemoveListener(OpenSettings);

        if (m_tutorialBtn != null)
            m_tutorialBtn.onClick.RemoveListener(TutorialFlow.Enter);
    }

    /// <summary>로그아웃하면 관문 화면으로 되돌린다.</summary>
    private void ReturnToAuthGate()
    {
        if (TryGetPanel(out SessionPanel session))
            session.ClosePanel();

        OpenPanel<AuthGatePanel>();
    }

    public static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OpenSettings() => OpenPanel<SettingsPanel>();
}
