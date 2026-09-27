using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 전역 파사드 — 모든 매니저 접근의 단일 경로. 매니저 필드는 ManagerHandler가 리플렉션으로 주입한다.
/// 새 매니저 추가: 베이스 상속 → private 필드 추가 → 그룹에 프로퍼티 추가.
/// </summary>
public class App : Singleton<App>
{
    #region 매니저 필드 — ManagerHandler가 주입 (타입당 하나만)
#pragma warning disable CS0649
    private SceneManagerBase m_sceneManager;

    private SessionManager m_sessionManager;
    private AuthBootstrap m_authBootstrap;
    private VivoxManager m_vivoxManager;
    private LoadingScreen m_loadingScreen;
    private SoundManager m_soundManager;

    private RoundManager m_roundManager;
    private RoundProgress m_roundProgress;
    private SuddenEventManager m_suddenEventManager;
    private WantedListManager m_wantedListManager;
    private ArrestJudge m_arrestJudge;
    private CriminalAssigner m_criminalAssigner;
    private NpcSpawner m_npcSpawner;
    private AppearanceAssigner m_appearanceAssigner;
    private DirectoryManager m_directoryManager;
    private WrongfulArrestPenalty m_wrongfulArrestPenalty;
    private TeamFund m_teamFund;
    private SessionRoster m_sessionRoster;
    private ShopPurchases m_shopPurchases;
    private MapSelection m_mapSelection;
    private SceneTransitionAnnouncer m_sceneTransitionAnnouncer;
    private FactionSymbolManager m_factionSymbolManager;
    private SceneReadyGate m_sceneReadyGate;
    private JailZone m_jailZone;
    private JailIntake m_jailIntake;
    private JailLock m_jailLock;
    private SettlementConfirmGate m_settlementConfirmGate;
    private EffectManager m_effectManager;
    private FxManager m_fxManager;

    private UIManagerBase m_uiManager;

    private CrosshairUI m_crosshairUI;
    private ChannelingGaugeUI m_channelingGaugeUI;
    private DamageVignetteUI m_damageVignetteUI;
    private SpeedVignetteUI m_speedVignetteUI;
    private TaserShockUI m_taserShockUI;
    private ToastView m_toastView;
    private SignalMessageView m_signalMessageView;
    private PromptView m_promptView;
    private InteractPromptView m_interactPromptView;
    private PortableMinimapHud m_portableMinimapHud;
#pragma warning restore CS0649
    #endregion

    #region 씬 상태 · 이벤트
    public static event UnityAction<EScene> OnSceneLoad;
    public static event UnityAction<EScene> OnSceneLoaded;

    public static EScene PrevScene { get; private set; }
    public static EScene CurrentScene { get; private set; }

    private static bool s_isLoading;

    /// <summary>씬 전환 단일 경로 — 세션 중이면 NGO 씬 동기화, 아니면 로컬 로드 (AppHelper가 분기).</summary>
    public static void LoadScene(EScene scene) => LoadSceneAsync(scene).Forget();

    /// <summary>정리 훅 → 로딩 화면 덮기 → 씬 로드 → 페이드 아웃 순으로 씬을 전환한다.</summary>
    public static async UniTask LoadSceneAsync(EScene scene)
    {
        if (s_isLoading)
        {
            Debug.LogWarning($"[App] 씬 전환이 진행 중이라 중복 요청을 무시합니다: {scene}");
            return;
        }
        s_isLoading = true;

        try
        {
            OnSceneLoad?.Invoke(scene);

            CancellationToken token = Application.exitCancellationToken;

            LoadingScreen loading = UI.Loading;
            bool fadeOnly = IsFadeOnlyTransition(scene);

            if (loading != null)
            {
                Net.SceneTransition?.AnnounceCover();

                await loading.ShowAsync(token, fadeOnly);
            }

            await AppHelper.LoadSceneAsync(
                scene,
                token,
                loading != null ? loading.ReportSceneLoadProgress : null
            );

            if (loading != null && !fadeOnly)
                loading.BeginSceneReadyWait();

            await WaitUntilSceneReadyAsync(token);

            if (loading != null)
                await loading.HideAsync(token);
        }
        finally
        {
            s_isLoading = false;
        }
    }

    private static bool IsFadeOnlyTransition(EScene next) =>
        CurrentScene == EScene.Title && next == EScene.Lobby;

    /// <summary>새 씬의 준비 완료를 기다리고, 그 사이 조준 윤곽선 워밍업을 끼워 넣는다.</summary>
    internal static async UniTask WaitUntilSceneReadyAsync(CancellationToken token)
    {
        InteractionFeedback.WarmUpInteractableOutlines();

        if (SceneFlow.Current != null)
            await SceneFlow.Current.WaitUntilReadyAsync(token);
    }

    /// <summary>AppHelper의 sceneLoaded 콜백에서만 호출 — 씬 상태 갱신 + 완료 이벤트.</summary>
    internal static void NotifySceneLoaded(EScene scene)
    {
        PrevScene = CurrentScene;
        CurrentScene = scene;
        OnSceneLoaded?.Invoke(scene);
    }
    #endregion

    #region 접근 그룹 — 사용처는 반드시 이 경로로
    public static class Net
    {
        public static SessionManager Session => Instance.m_sessionManager;
        public static AuthBootstrap Auth => Instance.m_authBootstrap;
        public static VivoxManager Vivox => Instance.m_vivoxManager;

        public static SceneTransitionAnnouncer SceneTransition =>
            Instance.m_sceneTransitionAnnouncer;
    }

    public static class Game
    {
        public static RoundManager Round => Instance.m_roundManager;

        public static RoundProgress RoundProgress => Instance.m_roundProgress;

        public static SuddenEventManager SuddenEvent => Instance.m_suddenEventManager;
        public static WantedListManager WantedList => Instance.m_wantedListManager;
        public static ArrestJudge ArrestJudge => Instance.m_arrestJudge;
        public static CriminalAssigner CriminalAssigner => Instance.m_criminalAssigner;
        public static NpcSpawner NpcSpawner => Instance.m_npcSpawner;
        public static AppearanceAssigner Appearance => Instance.m_appearanceAssigner;
        public static WrongfulArrestPenalty WrongfulArrestPenalty =>
            Instance.m_wrongfulArrestPenalty;
        public static TeamFund TeamFund => Instance.m_teamFund;

        public static SessionRoster Roster => Instance.m_sessionRoster;
        public static ShopPurchases ShopPurchases => Instance.m_shopPurchases;

        public static MapSelection MapSelection => Instance.m_mapSelection;
        public static DirectoryManager Directory => Instance.m_directoryManager;
        public static FactionSymbolManager FactionSymbol => Instance.m_factionSymbolManager;
        public static SceneReadyGate ReadyGate => Instance.m_sceneReadyGate;
        public static SettlementConfirmGate SettlementGate => Instance.m_settlementConfirmGate;

        public static JailZone Jail => Instance.m_jailZone;
        public static JailIntake JailIntake => Instance.m_jailIntake;
        public static JailLock JailLock => Instance.m_jailLock;

        public static FxManager Fx => Instance.m_fxManager;

        public static EffectManager Effect => Instance.m_effectManager;
    }

    public static SoundManager Sound => Instance.m_soundManager;

    public static class SceneFlow
    {
        public static SceneManagerBase Current => Instance.m_sceneManager;
        public static TitleManager Title => Instance.m_sceneManager as TitleManager;
        public static LobbyManager Lobby => Instance.m_sceneManager as LobbyManager;
        public static ShopManager Shop => Instance.m_sceneManager as ShopManager;
        public static InGameManager Game => Instance.m_sceneManager as InGameManager;
    }

    public static class UI
    {
        public static UIManagerBase Current => Instance.m_uiManager;
        public static TitleUIManager Title => Instance.m_uiManager as TitleUIManager;
        public static InGameUIManager Game => Instance.m_uiManager as InGameUIManager;

        public static CrosshairUI Crosshair => Instance.m_crosshairUI;
        public static ChannelingGaugeUI Gauge => Instance.m_channelingGaugeUI;
        public static DamageVignetteUI DamageVignette => Instance.m_damageVignetteUI;
        public static SpeedVignetteUI SpeedVignette => Instance.m_speedVignetteUI;
        public static TaserShockUI TaserShock => Instance.m_taserShockUI;
        public static ToastView Toast => Instance.m_toastView;
        public static SignalMessageView SignalMessage => Instance.m_signalMessageView;
        public static PromptView Prompt => Instance.m_promptView;
        public static InteractPromptView InteractPrompt => Instance.m_interactPromptView;
        public static PortableMinimapHud PortableMinimap => Instance.m_portableMinimapHud;

        public static LoadingScreen Loading => Instance.m_loadingScreen;
    }
    #endregion

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Reset();
        OnSceneLoad = null;
        OnSceneLoaded = null;
        PrevScene = EScene.None;
        CurrentScene = EScene.None;
        s_isLoading = false;
    }
}
