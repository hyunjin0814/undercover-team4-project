using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 씬 전환을 덮는 상주 로딩 화면 — 서버·오프라인은 App.LoadScene이, 클라는 NGO 씬 이벤트·전환 예고가 구동한다.
/// 완전히 불투명해진 화면이 렌더된 뒤 씬 로드가 시작되도록 페이드와 프레임 대기를 둔다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class LoadingScreen : CommonManagerBase
{
    private const int k_settleFrames = 3;

    private const float k_progressPerSecond = 2.5f;

    private const float k_loadTimeoutSeconds = 30f;

    private const float k_announceTimeoutSeconds = 10f;

    [Header("참조")]
    [SerializeField]
    private Canvas m_canvas;

    [SerializeField]
    private CanvasGroup m_canvasGroup;

    [Tooltip("비워도 됨 — 상태 문구")]
    [SerializeField]
    private TMP_Text m_statusText;

    [Header("진행률 (#582)")]
    [Tooltip("게이지바 — Image Type을 Filled로 둘 것")]
    [SerializeField]
    private Image m_progressFill;

    [Tooltip("게이지바 우측 퍼센트 숫자")]
    [SerializeField]
    private TMP_Text m_percentText;

    [Header("달리는 캐릭터 (#582)")]
    [Tooltip("전용 카메라·캐릭터가 있는 무대 — 로딩 중에만 켜서 렌더 비용을 없앤다")]
    [SerializeField]
    private GameObject m_runnerStage;

    [Tooltip("기본 상태 문구 — Common.Loading.Status")]
    [SerializeField]
    private LocalizedString m_defaultStatus;

    [Tooltip("씬 로드 후 런타임 스폰을 기다리는 동안의 문구 — Common.Loading.Preparing")]
    [SerializeField]
    private LocalizedString m_readyWaitStatus;

    [Header("페이드 전용 모드")]
    [Tooltip("페이드 전용 모드에서 화면을 덮는 검은 이미지 — 전체 스트레치, 알파 1")]
    [SerializeField]
    private GameObject m_fadeCover;

    [Tooltip("페이드 전용 모드에서 감출 로딩 표시 — 배경·게이지·문구 (러너 무대는 따로 처리)")]
    [SerializeField]
    private GameObject[] m_loadingVisuals;

    [Header("연출")]
    [Tooltip("페이드 인 시간(초). 0이면 즉시 덮는다. 이 시간만큼 씬 로드 시작이 늦어진다.")]
    [SerializeField]
    private float m_fadeInSeconds = 0.25f;

    [Tooltip("페이드 아웃 시간(초). 0이면 즉시 사라진다.")]
    [SerializeField]
    private float m_fadeOutSeconds = 0.35f;

    private NetworkSceneManager m_hookedSceneManager;

    private string m_clientLoadedScene;

    private bool m_isTrackingNetworkLoad;

    private LocalizedString m_boundStatus;

    private float m_targetProgress;
    private float m_shownProgress;

    private int m_shownPercent = -1;

    private int m_fadeGeneration;

    private bool m_isFadeOnly;

    public bool IsBusy { get; private set; }

    protected override void Awake()
    {
        base.Awake();
        SetVisible(false, 0f);
        SetStatus(null);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        UnbindStatus();
        HookSceneManager(null);
    }

    private void Update()
    {
        if (IsBusy)
            AdvanceProgress();

        RefreshNetworkHook();
    }

    #region 표시 제어 — App.LoadScene 파이프라인이 호출
    /// <summary>페이드 인으로 화면을 덮고 렌더될 때까지 대기한다. fadeOnly면 검은 화면만 페이드한다.</summary>
    public async UniTask ShowAsync(CancellationToken token = default, bool fadeOnly = false)
    {
        BeginShow(0f, fadeOnly);
        await FadeToAsync(1f, m_fadeInSeconds, token);
        await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);
    }

    /// <summary>대기 없이 즉시 덮는다 — 이미 로드가 시작돼 기다릴 여유가 없는 클라이언트 경로용.</summary>
    public void ShowInstant() => BeginShow(1f, false);

    private void BeginShow(float alpha, bool fadeOnly)
    {
        IsBusy = true;
        m_isFadeOnly = fadeOnly;
        m_targetProgress = 0f;
        m_shownProgress = 0f;
        m_shownPercent = -1;
        RenderProgress();
        SetStatus(null);
        m_fadeGeneration++;
        SetVisible(true, alpha);
    }

    /// <summary>페이드 아웃 후 화면을 내린다.</summary>
    public async UniTask HideAsync(CancellationToken token = default)
    {
        if (!IsBusy)
            return;

        m_targetProgress = 1f;
        m_shownProgress = 1f;
        RenderProgress();

        await FadeToAsync(0f, m_fadeOutSeconds, token);
        SetVisible(false, 0f);
        IsBusy = false;
    }

    /// <summary>씬 로드 진행률(0~1) 보고 — App.LoadScene 파이프라인이 매 프레임 부른다.</summary>
    public void ReportSceneLoadProgress(float ratio01) => SetTargetProgress(ratio01);

    /// <summary>씬 로드 완료 후 런타임 스폰 대기 단계로 넘어가 게이지를 채우고 문구를 바꾼다.</summary>
    public void BeginSceneReadyWait()
    {
        SetTargetProgress(1f);
        SetStatus(m_readyWaitStatus);
    }

    private void SetTargetProgress(float value) =>
        m_targetProgress = Mathf.Max(m_targetProgress, Mathf.Clamp01(value));

    private void AdvanceProgress()
    {
        if (Mathf.Approximately(m_shownProgress, m_targetProgress))
            return;

        m_shownProgress = Mathf.MoveTowards(
            m_shownProgress,
            m_targetProgress,
            k_progressPerSecond * Time.unscaledDeltaTime
        );
        RenderProgress();
    }

    private void RenderProgress()
    {
        if (m_progressFill != null)
            m_progressFill.fillAmount = m_shownProgress;

        if (m_percentText == null)
            return;

        int percent = Mathf.RoundToInt(m_shownProgress * 100f);
        if (percent == m_shownPercent)
            return;

        m_shownPercent = percent;
        m_percentText.text = percent + "%";
    }

    /// <summary>상태 문구를 교체한다. null이면 기본 문구로 돌아간다.</summary>
    public void SetStatus(LocalizedString status)
    {
        LocalizedString next = status ?? m_defaultStatus;

        if (m_statusText == null)
            return;

        if (next == null || next.IsEmpty)
        {
            Debug.LogWarning("[LoadingScreen] 상태 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindStatus();

        m_boundStatus = next;
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

    /// <summary>실시간 기준으로 알파를 목표값까지 옮긴다. 다른 페이드가 끼어들면 중단한다.</summary>
    private async UniTask FadeToAsync(float target, float seconds, CancellationToken token)
    {
        if (m_canvasGroup == null)
            return;

        int generation = ++m_fadeGeneration;

        if (seconds <= 0f)
        {
            m_canvasGroup.alpha = target;
            return;
        }

        float from = m_canvasGroup.alpha;
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            m_canvasGroup.alpha = Mathf.Lerp(from, target, elapsed / seconds);
            await UniTask.Yield(PlayerLoopTiming.Update, token);

            if (generation != m_fadeGeneration)
                return;
        }

        m_canvasGroup.alpha = target;
    }

    private void SetVisible(bool visible, float alpha)
    {
        if (m_canvas != null)
            m_canvas.enabled = visible;

        bool showLoadingVisuals = visible && !m_isFadeOnly;

        if (m_runnerStage != null)
            m_runnerStage.SetActive(showLoadingVisuals);

        if (m_loadingVisuals != null)
        {
            foreach (GameObject visual in m_loadingVisuals)
                if (visual != null)
                    visual.SetActive(showLoadingVisuals);
        }

        if (m_fadeCover != null)
            m_fadeCover.SetActive(visible && m_isFadeOnly);

        if (m_canvasGroup == null)
            return;

        m_canvasGroup.alpha = alpha;
        m_canvasGroup.blocksRaycasts = visible;
        m_canvasGroup.interactable = visible;
    }
    #endregion

    #region 클라이언트 자동 경로 — NGO 씬 동기화로 끌려오는 쪽
    private void RefreshNetworkHook()
    {
        NetworkManager net = NetworkManager.Singleton;
        NetworkSceneManager current = net != null && net.IsListening ? net.SceneManager : null;

        if (!ReferenceEquals(current, m_hookedSceneManager))
            HookSceneManager(current);
    }

    private void HookSceneManager(NetworkSceneManager target)
    {
        if (m_hookedSceneManager != null)
        {
            m_hookedSceneManager.OnLoad -= HandleNetworkLoad;
            m_hookedSceneManager.OnLoadComplete -= HandleNetworkLoadComplete;
        }

        m_hookedSceneManager = target;

        if (m_hookedSceneManager != null)
        {
            m_hookedSceneManager.OnLoad += HandleNetworkLoad;
            m_hookedSceneManager.OnLoadComplete += HandleNetworkLoadComplete;
        }
    }

    private void HandleNetworkLoad(
        ulong clientId,
        string sceneName,
        LoadSceneMode mode,
        AsyncOperation operation
    )
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net == null || net.IsServer)
            return;

        if (clientId != net.LocalClientId || m_isTrackingNetworkLoad)
            return;

        CoverUntilLoadedAsync(sceneName, operation).Forget();
    }

    private void HandleNetworkLoadComplete(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        NetworkManager net = NetworkManager.Singleton;
        if (net != null && clientId == net.LocalClientId)
            m_clientLoadedScene = sceneName;
    }

    /// <summary>서버의 전환 예고를 받아 미리 덮는다 — 완료 대기·내리기는 뒤이어 올 씬 이벤트가 맡는다.</summary>
    public void CoverForIncomingSceneChange()
    {
        if (IsBusy)
            return;

        BeginShow(0f, false);
        FadeToAsync(1f, m_fadeInSeconds, this.GetCancellationTokenOnDestroy()).Forget();
        WaitForAnnouncedLoadAsync().Forget();
    }

    private async UniTaskVoid WaitForAnnouncedLoadAsync()
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        float deadline = Time.realtimeSinceStartup + k_announceTimeoutSeconds;
        while (!m_isTrackingNetworkLoad && IsBusy && Time.realtimeSinceStartup < deadline)
            await UniTask.Yield(PlayerLoopTiming.Update, token);

        if (m_isTrackingNetworkLoad || !IsBusy)
            return;

        Debug.LogWarning(
            "[LoadingScreen] 전환 예고 뒤 씬 로드가 시작되지 않았습니다 — 로딩 화면을 내립니다."
        );
        await HideAsync(token);
    }

    private async UniTaskVoid CoverUntilLoadedAsync(string sceneName, AsyncOperation operation)
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();
        m_clientLoadedScene = null;
        m_isTrackingNetworkLoad = true;

        if (!IsBusy)
            ShowInstant();

        try
        {
            float deadline = Time.realtimeSinceStartup + k_loadTimeoutSeconds;
            while (
                m_clientLoadedScene != sceneName
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsListening
                && Time.realtimeSinceStartup < deadline
            )
            {
                if (operation != null)
                    ReportSceneLoadProgress(operation.progress);

                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            if (m_clientLoadedScene == sceneName)
            {
                BeginSceneReadyWait();

                await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);

                await App.WaitUntilSceneReadyAsync(token);
            }
            else
            {
                Debug.LogWarning(
                    $"[LoadingScreen] '{sceneName}' 로드 완료를 확인하지 못했습니다 — 로딩 화면을 내립니다."
                );
            }

            await HideAsync(token);
        }
        finally
        {
            m_isTrackingNetworkLoad = false;
        }
    }
    #endregion
}
