using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

[LocalizedEnum("TitleTable", "Title.AuthStatus.")]
public enum EAuthStatus
{
    Busy = 0,
    LinkSucceeded = 1,
    SignInSucceeded = 2,
    NewAnonymousStarted = 3,
    ConfirmUnavailableForLink = 4,
    ConfirmUnavailableForSwitch = 5,
    SigningIn = 6,
}

/// <summary>
/// 타이틀 로그인 관문 — 게스트 시작·로그인·회원가입(익명 계정 승격) 중 하나를 통과하면 세션 화면을 연다.
/// 열지 여부는 TitleUIManager가 정한다.
/// </summary>
public class AuthGatePanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    [Header("계정 입력")]
    [SerializeField]
    private TMP_InputField m_usernameInput;

    [SerializeField]
    private TMP_InputField m_passwordInput;

    [Header("버튼")]
    [SerializeField]
    private Button m_signInButton;

    [SerializeField]
    private Button m_signUpButton;

    [SerializeField]
    private Button m_guestButton;

    [Header("상태 문구")]
    [SerializeField]
    private TMP_Text m_statusText;

    [Header("연출")]
    [Tooltip("관문 배경(Curtain) 페이드 인 — 비우면 페이드 없이 바로 로그인 폼을 보여준다")]
    [SerializeField]
    private CanvasGroup m_curtainCanvasGroup;

    [Tooltip("로그인 폼 — 배경이 페이드 인된 뒤에만 활성화한다")]
    [SerializeField]
    private GameObject m_windowRoot;

    [Tooltip("배경 페이드 인 시간(초)")]
    [SerializeField]
    private float m_curtainFadeSeconds = 0.5f;

    private const string k_table = "TitleTable";
    private const string k_statusPrefix = "Title.AuthStatus.";
    private const string k_linkConfirmKey = "Title.Auth.LinkConfirm";

    private const string k_linkConfirmSwitchKey = "Title.Auth.LinkConfirmSwitch";

    private bool m_isBusy;

    private LocalizedMessage m_status;

    private bool m_statusIsProgress;

    private AuthBootstrap Auth => App.Net.Auth;

    private void OnEnable()
    {
        m_signInButton.onClick.AddListener(HandleSignInClicked);
        m_signUpButton.onClick.AddListener(HandleSignUpClicked);
        m_guestButton.onClick.AddListener(HandleGuestClicked);

        m_usernameInput.characterLimit = AccountCredentials.MaxUsernameLength;
        m_passwordInput.characterLimit = AccountCredentials.MaxPasswordLength;
        m_passwordInput.contentType = TMP_InputField.ContentType.Password;
        m_passwordInput.ForceLabelUpdate();

        if (Auth != null)
        {
            Auth.OnSignedIn += Refresh;
            Auth.OnSignedOut += Refresh;
            Auth.OnSigningInChanged += Refresh;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh();
    }

    private void OnDisable()
    {
        m_signInButton.onClick.RemoveListener(HandleSignInClicked);
        m_signUpButton.onClick.RemoveListener(HandleSignUpClicked);
        m_guestButton.onClick.RemoveListener(HandleGuestClicked);

        if (Auth != null)
        {
            Auth.OnSignedIn -= Refresh;
            Auth.OnSignedOut -= Refresh;
            Auth.OnSigningInChanged -= Refresh;
        }

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RenderStatus();
        Refresh();
    }

    #region 연출
    private CanvasGroup m_rootGroup;

    private bool m_isPassing;

    protected override void Awake()
    {
        base.Awake();

        if (!m_panelRoot.TryGetComponent(out m_rootGroup))
            m_rootGroup = m_panelRoot.AddComponent<CanvasGroup>();
    }

    public override void OpenPanel()
    {
        m_isPassing = false;
        SetWindowVisible(false);

        m_rootGroup.alpha = 1f;
        m_rootGroup.blocksRaycasts = true;

        base.OpenPanel();
        FadeInCurtainAsync(this.GetCancellationTokenOnDestroy()).Forget();
    }

    private async UniTaskVoid FadeInCurtainAsync(CancellationToken token)
    {
        await UIFade.ToAsync(m_curtainCanvasGroup, 0f, 1f, m_curtainFadeSeconds, token);
        SetWindowVisible(true);
    }

    private void SetWindowVisible(bool visible)
    {
        if (m_windowRoot != null)
            m_windowRoot.SetActive(visible);
    }
    #endregion

    #region 통과
    /// <summary>관문을 넘긴다 — 다시 오지 않도록 표시하고 세션 화면으로 넘어간다.</summary>
    private void Pass()
    {
        if (Auth != null)
            Auth.MarkAuthGatePassed();

        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SessionPanel session))
            session.OpenPanel();
        else
            Debug.LogError("[AuthGatePanel] SessionPanel이 씬에 없어 세션 화면을 열지 못했습니다.", this);

        FadeOutThenCloseAsync(this.GetCancellationTokenOnDestroy()).Forget();
    }

    /// <summary>커튼과 로그인 폼을 함께 페이드해 아래의 세션 화면을 드러낸다.</summary>
    private async UniTaskVoid FadeOutThenCloseAsync(CancellationToken token)
    {
        m_isPassing = true;

        m_rootGroup.blocksRaycasts = false;

        await UIFade.ToAsync(m_rootGroup, 1f, 0f, m_curtainFadeSeconds, token);

        if (m_isPassing)
            ClosePanel();
    }
    #endregion

    #region 게스트
    private void HandleGuestClicked() => GuestAsync().Forget();

    private async UniTaskVoid GuestAsync()
    {
        if (m_isBusy || Auth == null)
            return;

        if (Auth.IsSignedIn)
        {
            Pass();
            return;
        }

        m_isBusy = true;
        Refresh();
        try
        {
            await Auth.InitializeAndSignInAsync();
            Pass();
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }
    #endregion

    #region 로그인
    private void HandleSignInClicked() => SignInAsync().Forget();

    private async UniTaskVoid SignInAsync()
    {
        if (m_isBusy || Auth == null)
            return;

        m_isBusy = true;
        Refresh();
        try
        {
            await Auth.SignInWithAccountAsync(m_usernameInput.text, m_passwordInput.text);
            m_passwordInput.text = string.Empty;
            SetStatus(Status(EAuthStatus.SignInSucceeded));
            Pass();
        }
        catch (RequestFailedException ex)
        {
            SetStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }
    #endregion

    #region 회원가입 (익명 → 정식 승격)
    private void HandleSignUpClicked()
    {
        if (m_isBusy || Auth == null)
            return;

        string id = m_usernameInput.text?.Trim() ?? string.Empty;
        string pw = m_passwordInput.text ?? string.Empty;
        EAccountValidation result = AccountCredentials.Validate(id, pw);
        if (result != EAccountValidation.Ok)
        {
            SetStatus(AccountCredentials.Describe(result));
            return;
        }

        if (!TryGetConfirmPanel(out AccountConfirmPanel confirm))
        {
            SetStatus(Status(EAuthStatus.ConfirmUnavailableForLink));
            return;
        }

        string previous = Auth.AccountUsername;
        string message = string.IsNullOrEmpty(previous)
            ? LocalizedStrings.Get(k_table, k_linkConfirmKey, id)
            : LocalizedStrings.Get(k_table, k_linkConfirmSwitchKey, id, previous);

        confirm.Prepare(message, () => SignUpAsync(id, pw).Forget());
        confirm.OpenPanel();
    }

    private async UniTaskVoid SignUpAsync(string username, string password)
    {
        if (m_isBusy || Auth == null)
            return;

        m_isBusy = true;
        Refresh();
        try
        {
            if (!Auth.IsSignedIn)
                await Auth.InitializeAndSignInAsync();

            await Auth.LinkAccountAsync(username, password);
            m_passwordInput.text = string.Empty;
            SetStatus(Status(EAuthStatus.LinkSucceeded));
            Pass();
        }
        catch (RequestFailedException ex)
        {
            SetStatus(AccountCredentials.DescribeError(ex));
        }
        catch (LocalizedMessageException ex)
        {
            SetStatus(ex.Reason);
        }
        catch (Exception ex)
        {
            SetStatus(LocalizedMessage.Literal(ex.Message));
        }
        finally
        {
            m_isBusy = false;
            Refresh();
        }
    }

    private static bool TryGetConfirmPanel(out AccountConfirmPanel panel)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out panel))
            return true;

        Debug.LogError("[AuthGatePanel] AccountConfirmPanel이 씬에 없어 회원가입을 중단했습니다.");
        panel = null;
        return false;
    }
    #endregion

    #region 표시
    private static LocalizedMessage Status(EAuthStatus status) =>
        LocalizedMessage.Of(k_table, k_statusPrefix + status);

    /// <summary>결과 문구 — 다음 조작 때까지 남는다.</summary>
    private void SetStatus(in LocalizedMessage message)
    {
        m_status = message;
        m_statusIsProgress = false;
        RenderStatus();
    }

    /// <summary>진행 문구 — 상황이 끝나면 Refresh가 지운다.</summary>
    private void SetProgressStatus(in LocalizedMessage message)
    {
        m_status = message;
        m_statusIsProgress = true;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (m_statusText != null)
            m_statusText.text = m_status.Resolve();
    }

    private void Refresh()
    {
        bool signedIn = Auth != null && Auth.IsSignedIn;
        bool signingIn = Auth != null && Auth.IsSigningIn;

        bool ready = !signingIn && !m_isBusy;
        m_signInButton.interactable = ready;
        m_guestButton.interactable = ready;

        m_signUpButton.interactable = ready;

        m_usernameInput.interactable = ready;
        m_passwordInput.interactable = ready;

        if (m_isBusy)
            SetProgressStatus(Status(EAuthStatus.Busy));
        else if (signingIn)
            SetProgressStatus(Status(EAuthStatus.SigningIn));
        else if (m_statusIsProgress)
            SetStatus(LocalizedMessage.None);
    }
    #endregion
}
