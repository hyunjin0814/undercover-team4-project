using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// 타이틀의 세션 화면 — 세션 생성(호스트)·코드 참가·이어하기를 담당한다.
/// 로그인 관문을 통과해야 열린다.
/// </summary>
public class SessionPanel : PanelBase
{
    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    /// <summary>배경을 즉시 켜고 패널을 연다(관문 통과 경로).</summary>
    public override void OpenPanel()
    {
        m_closeRequested = false;
        SetBackdropAlpha(1f);
        OpenNow();
    }

    /// <summary>배경을 페이드 인하며 패널을 연다(씬 진입 직후 전용).</summary>
    public void OpenWithBackdropFade()
    {
        m_closeRequested = false;
        OpenAfterBackdropFadeAsync().Forget();
    }

    public override void ClosePanel()
    {
        m_closeRequested = true;
        base.ClosePanel();
    }

    private bool m_closeRequested;

    private async UniTaskVoid OpenAfterBackdropFadeAsync()
    {
        await UIFade.ToAsync(
            m_backdropCanvasGroup,
            0f,
            1f,
            m_backdropFadeSeconds,
            this.GetCancellationTokenOnDestroy()
        );

        SetBackdropAlpha(1f);

        if (!m_closeRequested)
            OpenNow();
    }

    private void SetBackdropAlpha(float alpha)
    {
        if (m_backdropCanvasGroup != null)
            m_backdropCanvasGroup.alpha = alpha;
    }

    /// <summary>패널을 실제로 열고, 필요하면 튜토리얼 권유 창을 띄운다.</summary>
    private void OpenNow()
    {
        base.OpenPanel();

        if (TutorialFlow.WasOffered)
            return;

        if (App.UI.Current != null && App.UI.Current.OpenPanel<TutorialConfirmPanel>())
            TutorialFlow.MarkOffered();
    }

    [Header("연출")]
    [Tooltip("세션 화면 배경(Backdrop) 페이드 인 — 비우면 페이드 없이 바로 열린다")]
    [SerializeField]
    private CanvasGroup m_backdropCanvasGroup;

    [Tooltip("배경 페이드 인 시간(초)")]
    [SerializeField]
    private float m_backdropFadeSeconds = 0.5f;

    [Header("UI 참조")]
    [SerializeField]
    private Button m_createButton;

    [Tooltip("이어하기 — 저장된 판이 없으면 눌렀을 때 사유를 띄운다 (#373·#704)")]
    [SerializeField]
    private Button m_continueButton;

    [FormerlySerializedAs("m_joinButton")]
    [SerializeField]
    private Button m_joinCodeButton;

    [SerializeField]
    private TMP_Text m_statusText;

    [Header("상태 문구 (TitleTable)")]
    [Tooltip("익명 로그인 진행 중 — Title.Session.Status.SigningIn")]
    [SerializeField]
    private LocalizedString m_statusSigningIn;

    [Tooltip("로그인 완료 — Title.Session.Status.SignedIn ({0}=플레이어 ID)")]
    [SerializeField]
    private LocalizedString m_statusSignedIn;

    [Tooltip("이어할 판이 있음 — Title.Session.Status.SaveFound ({0}=라운드 번호)")]
    [SerializeField]
    private LocalizedString m_statusSaveFound;

    [Tooltip("이어할 판이 없음 — Title.Session.Status.NoSave")]
    [SerializeField]
    private LocalizedString m_statusNoSave;

    [Tooltip("세션 생성 중 — Title.Session.Status.Creating")]
    [SerializeField]
    private LocalizedString m_statusCreating;

    [Tooltip("세션 생성됨 — Title.Session.Status.Created ({0}=참가 코드)")]
    [SerializeField]
    private LocalizedString m_statusCreated;

    [Tooltip("생성 실패 — Title.Session.Status.CreateFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusCreateFailed;

    [Tooltip("세션 참가 중 — Title.Session.Status.Joining")]
    [SerializeField]
    private LocalizedString m_statusJoining;

    [Tooltip("접속 중 — Title.Session.Status.Connecting")]
    [SerializeField]
    private LocalizedString m_statusConnecting;

    [Tooltip("참가 실패 — Title.Session.Status.JoinFailed ({0}=예외 메시지)")]
    [SerializeField]
    private LocalizedString m_statusJoinFailed;

    [Tooltip("게임 버전 불일치 — Title.Session.Status.VersionMismatch ({0}=내 버전, {1}=방 버전)")]
    [SerializeField]
    private LocalizedString m_statusVersionMismatch;

    private bool m_isBusy;

    private bool m_showingMismatch;

    private UniTask<bool> m_saveCheck;

    private LocalizedString m_boundStatus;

    private void OnEnable()
    {
        m_createButton.onClick.AddListener(HandleCreateClicked);
        m_continueButton.onClick.AddListener(HandleContinueClicked);
        m_joinCodeButton.onClick.AddListener(HandleJoinCodeClicked);

        AuthBootstrap auth = App.Net.Auth;
        if (auth == null)
            return;

        if (auth.IsSignedIn)
        {
            HandleSignedIn();
            return;
        }

        SetButtonsInteractable(false);
        SetStatus(m_statusSigningIn);
        auth.OnSignedIn += HandleSignedIn;
    }

    private void OnDisable()
    {
        m_createButton.onClick.RemoveListener(HandleCreateClicked);
        m_continueButton.onClick.RemoveListener(HandleContinueClicked);
        m_joinCodeButton.onClick.RemoveListener(HandleJoinCodeClicked);

        if (App.Net.Auth != null)
            App.Net.Auth.OnSignedIn -= HandleSignedIn;

        UnbindStatus();
    }

    private void HandleSignedIn()
    {
        SetButtonsInteractable(true);
        SetStatus(m_statusSignedIn, App.Net.Auth.PlayerId);
        RefreshSaveAsync().Forget();
        ShowPendingVersionMismatch();
    }

    /// <summary>SessionManager가 보관한 버전 불일치 사유를 띄운다.</summary>
    private void ShowPendingVersionMismatch()
    {
        if (m_statusText == null || App.Net.Session == null)
            return;

        SessionVersionMismatchException pending = App.Net.Session.TakePendingVersionMismatch();
        if (pending == null)
            return;

        Debug.Log(
            $"[SessionPanel] 버전 불일치 안내 / 내 버전 {pending.LocalVersion}, 방 버전 {pending.SessionVersion}"
        );
        SetStatus(m_statusVersionMismatch, pending.LocalVersion, pending.SessionVersion);
        m_showingMismatch = true;
    }

    private async UniTaskVoid RefreshSaveAsync()
    {
        m_saveCheck = SaveService.RefreshAsync().Preserve();
        bool hasSave = await m_saveCheck;

        if (m_statusText == null || m_isBusy || m_showingMismatch)
            return;

        if (hasSave)
            SetStatus(m_statusSaveFound, SaveService.SavedRound);
    }

    private void SetButtonsInteractable(bool interactable)
    {
        m_createButton.interactable = interactable;
        m_continueButton.interactable = interactable;
        m_joinCodeButton.interactable = interactable;
    }

    private void HandleCreateClicked() => CreateAsync(continueSave: false).Forget();

    private void HandleContinueClicked() => ContinueAsync().Forget();

    /// <summary>이어하기 — 세이브가 없으면 세션을 만들지 않고 사유를 띄운다.</summary>
    private async UniTaskVoid ContinueAsync()
    {
        if (m_isBusy)
            return;

        bool hasSave = await m_saveCheck;

        if (m_statusText == null || !isActiveAndEnabled)
            return;

        if (!hasSave)
        {
            SetStatus(m_statusNoSave);
            return;
        }

        CreateAsync(continueSave: true).Forget();
    }

    private void HandleJoinCodeClicked()
    {
        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out JoinCodePanel panel))
        {
            Debug.LogError("[SessionPanel] JoinCodePanel이 씬에 없어 코드 입력을 열지 못했습니다.");
            return;
        }

        panel.Prepare(code => JoinAsync(code).Forget());
        panel.OpenPanel();
    }

    /// <summary>세션을 생성한다. continueSave면 저장된 판을 이어서 시작한다.</summary>
    private async UniTaskVoid CreateAsync(bool continueSave)
    {
        if (m_isBusy)
            return;
        m_isBusy = true;

        if (continueSave)
            SaveService.UseSave();
        else
            SaveService.StartFresh();

        SetStatus(m_statusCreating);
        try
        {
            string code = await App.Net.Session.CreateSessionAsync();
            SetStatus(m_statusCreated, code);
            App.SceneFlow.Title.StartGame();
        }
        catch (Exception e)
        {
            SetStatus(m_statusCreateFailed, e.Message);
            SaveService.StartFresh();
            m_isBusy = false;
        }
    }

    private async UniTaskVoid JoinAsync(string code)
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_showingMismatch = false;
        SetStatus(m_statusJoining);
        try
        {
            await App.Net.Session.JoinByCodeAsync(code);
            SetStatus(m_statusConnecting);
        }
        catch (SessionVersionMismatchException)
        {
            ShowPendingVersionMismatch();
            m_isBusy = false;
        }
        catch (Exception e)
        {
            SetStatus(m_statusJoinFailed, e.Message);
            m_isBusy = false;
        }
    }

    /// <summary>상태 문구를 교체하고 언어 변경을 구독한다.</summary>
    private void SetStatus(LocalizedString message, params object[] args)
    {
        if (m_statusText == null || message == null || message.IsEmpty)
            return;

        UnbindStatus();

        message.Arguments = (args != null && args.Length > 0) ? args : null;

        m_boundStatus = message;
        m_boundStatus.StringChanged += HandleStatusChanged;
    }

    private void HandleStatusChanged(string localized)
    {
        if (m_statusText != null)
            m_statusText.text = localized;
    }

    private void UnbindStatus()
    {
        if (m_boundStatus == null)
            return;

        m_boundStatus.StringChanged -= HandleStatusChanged;
        m_boundStatus = null;
    }
}
