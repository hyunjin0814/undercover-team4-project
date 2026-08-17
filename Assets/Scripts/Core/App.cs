using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 전역 파사드 — 모든 매니저 접근의 단일 경로. (아키텍처 규칙: 매니저 접근은 App 경유만 허용)
/// 매니저 필드는 ManagerHandler가 리플렉션으로 주입/해제한다. 직접 대입 금지.
/// 새 매니저 추가 = ① 베이스 상속 ② 아래에 private 필드 추가 ③ 그룹에 프로퍼티 추가.
/// 등록 기준: 씬에 개념적으로 하나뿐인 서비스가, 두 개 이상의 도메인에서 참조될 때.
/// </summary>
public class App : Singleton<App>
{
    #region 매니저 필드 — ManagerHandler가 주입 (타입당 하나만)
#pragma warning disable CS0649 // 리플렉션으로만 할당되는 필드 — "할당되지 않음" 경고는 오탐
    // 씬 매니저 (씬 전환 시 교체됨)
    private SceneManagerBase m_sceneManager;

    // 상주 매니저 (AppBootstrap 프리팹 · DontDestroyOnLoad — 3단계에서 배치)
    private SessionManager m_sessionManager;
    private AuthBootstrap m_authBootstrap;
    private VivoxManager m_vivoxManager;
    private LoadingScreen m_loadingScreen;
    private SoundManager m_soundManager;

    // 인게임 매니저 (Main Scene)
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
    private FactionSymbolManager m_factionSymbolManager;
    private SceneReadyGate m_sceneReadyGate;
    private JailZone m_jailZone;
    private JailIntake m_jailIntake;
    private JailLock m_jailLock;
    private SettlementConfirmGate m_settlementConfirmGate;
    private EffectManager m_effectManager;
    private FxManager m_fxManager;

    // UI 매니저 (씬 전환 시 교체됨)
    private UIManagerBase m_uiManager;

    // 로컬 HUD (런타임 생성 프리팹 — 생성/파괴 시 자동 등록/해제)
    private CrosshairUI m_crosshairUI;
    private ChannelingGaugeUI m_channelingGaugeUI;
    private DamageVignetteUI m_damageVignetteUI;
    private TaserShockUI m_taserShockUI;
    private ToastView m_toastView;
    private SignalMessageView m_signalMessageView;
    private PromptView m_promptView;
    private InteractPromptView m_interactPromptView;
    private SecretFavorHud m_secretFavorHud;
#pragma warning restore CS0649
    #endregion

    #region 씬 상태 · 이벤트
    public static event UnityAction<EScene> OnSceneLoad; // 언로드 직전 (정리 작업 훅)
    public static event UnityAction<EScene> OnSceneLoaded; // 새 씬 로드 완료

    public static EScene PrevScene { get; private set; }
    public static EScene CurrentScene { get; private set; }

    // 전환이 끝나기 전에 다른 LoadScene이 겹치면 로딩 화면 표시 상태가 꼬인다 — 재진입 가드.
    private static bool s_isLoading;

    /// <summary>씬 전환 단일 경로 — 세션 중이면 NGO 씬 동기화, 아니면 로컬 로드 (AppHelper가 분기).</summary>
    public static void LoadScene(EScene scene) => LoadSceneAsync(scene).Forget();

    /// <summary>
    /// 씬 전환 + 로딩 화면 파이프라인 (#403). 전환 완료까지 기다려야 하는 호출부만 이쪽을 await 한다.
    /// 순서: 정리 훅 → 화면 덮기(렌더 보장) → 실제 로드 → 페이드 아웃.
    /// </summary>
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

            // App은 MonoBehaviour가 아니라 파괴 토큰이 없다 — 플레이 종료 시 취소되는 토큰을 쓴다
            CancellationToken token = Application.exitCancellationToken;

            // 에디터에서 씬을 직접 Play하면 AppBootstrap이 없어 null일 수 있다 — 그때는 그냥 덮지 않는다
            LoadingScreen loading = ShouldCoverWithLoadingScreen(scene) ? UI.Loading : null;

            if (loading != null)
                await loading.ShowAsync(token); // 덮은 화면이 실제로 렌더될 때까지 대기

            // 게이지바가 실측할 수 있는 구간은 여기까지다 (#582)
            await AppHelper.LoadSceneAsync(
                scene,
                token,
                loading != null ? loading.ReportSceneLoadProgress : null
            );

            // 아래 대기는 진척을 알 수 없다 — 게이지를 채우고 문구로 바꿔 알린다 (#582)
            if (loading != null)
                loading.BeginSceneReadyWait();

            // 씬 오브젝트는 활성화 프레임에 다 섰지만 런타임 스폰(NPC 등)은 아직이다 — 씬이 스스로 보고한다
            await WaitUntilSceneReadyAsync(token);

            if (loading != null)
                await loading.HideAsync(token);
        }
        finally
        {
            s_isLoading = false;
        }
    }

    // Title → Lobby는 세션 생성 UI가 이미 진행 상태를 보여주고 있어 덮지 않는다 (#403).
    private static bool ShouldCoverWithLoadingScreen(EScene next) =>
        !(CurrentScene == EScene.Title && next == EScene.Lobby);

    /// <summary>
    /// 새 씬의 준비 완료를 기다린다 (#403). 씬 매니저는 활성화 프레임에 이미 App에 등록돼 있어 그대로 물으면 된다.
    /// 클라이언트는 이 경로를 타지 않으므로 LoadingScreen이 같은 대기를 따로 건다.
    /// </summary>
    internal static UniTask WaitUntilSceneReadyAsync(CancellationToken token) =>
        SceneFlow.Current != null
            ? SceneFlow.Current.WaitUntilReadyAsync(token)
            : UniTask.CompletedTask;

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
    }

    public static class Game
    {
        public static RoundManager Round => Instance.m_roundManager;

        // 세션 내 라운드 진행도 (#377). 세션 시작 시 1회 스폰되는 상주 홀더라 씬을 넘어 산다(TeamFund와 같은 구조).
        // 오프라인 단독 Play에는 상주 오브젝트가 없어 null — 사용처는 1라운드로 취급할 것
        public static RoundProgress RoundProgress => Instance.m_roundProgress;

        // 개별 돌발 이벤트(먹통 등)는 App에 올리지 않는다 — 이벤트마다 필드가 늘어나는 대신
        // SuddenEvent.GetEvent<T>()로 물어본다 (#372 리뷰, R3).
        public static SuddenEventManager SuddenEvent => Instance.m_suddenEventManager;
        public static WantedListManager WantedList => Instance.m_wantedListManager;
        public static ArrestJudge ArrestJudge => Instance.m_arrestJudge;
        public static CriminalAssigner CriminalAssigner => Instance.m_criminalAssigner;
        public static NpcSpawner NpcSpawner => Instance.m_npcSpawner;
        public static AppearanceAssigner Appearance => Instance.m_appearanceAssigner;
        public static WrongfulArrestPenalty WrongfulArrestPenalty =>
            Instance.m_wrongfulArrestPenalty;
        public static TeamFund TeamFund => Instance.m_teamFund;

        // 세션 접속자 명부 (#598). TeamFund와 같은 세션 상주 홀더라 어느 씬에서도 살아 있지만,
        // 세션 없이 씬을 직접 Play하면 스폰되지 않아 null이다 — 사용처는 ?. 가드 필수
        public static SessionRoster Roster => Instance.m_sessionRoster;
        public static ShopPurchases ShopPurchases => Instance.m_shopPurchases;

        // 다음 라운드로 갈 맵 (#578). TeamFund와 같은 세션 상주 홀더라 어느 씬에서도 살아 있지만,
        // 세션 없이 씬을 직접 Play하면 스폰되지 않아 null이다 — 사용처는 ?. 가드 필수
        public static MapSelection MapSelection => Instance.m_mapSelection;
        public static DirectoryManager Directory => Instance.m_directoryManager;
        public static FactionSymbolManager FactionSymbol => Instance.m_factionSymbolManager;
        public static SceneReadyGate ReadyGate => Instance.m_sceneReadyGate; // 전원 준비 완료 게이트 (#410). 게임 씬에만 있으므로 다른 씬에서는 null
        public static SettlementConfirmGate SettlementGate => Instance.m_settlementConfirmGate; // 전원 정산 확인 게이트 (#509). 마찬가지로 게임 씬 전용

        // 감옥 시설 셋 — 같은 오브젝트(Jail.prefab)에 얹혀 있고 게임 씬에만 있다. 다른 씬에서는 null이라
        // 읽는 쪽이 ?. 로 받는다 (ReadyGate·SettlementGate와 같은 방침). (#592)
        public static JailZone Jail => Instance.m_jailZone;
        public static JailIntake JailIntake => Instance.m_jailIntake;
        public static JailLock JailLock => Instance.m_jailLock;

        // 일회성 연출의 단일 창구 (#532) — "무슨 일이 일어났는가" 하나로 먼지+소리를 내고,
        // 서버 판정이면 전 피어에 전파한다. 일회성 연출은 이쪽을 부른다.
        // 인게임 씬 매니저라 로비·상점에서는 null — 사용처는 ?. 가드 필수
        public static FxManager Fx => Instance.m_fxManager;

        // 파티클 재생 + 풀링 (#478). Fx가 쓰는 아래 계층이다 — 소리 없이 파티클만 낼 때만 직접 부른다
        public static EffectManager Effect => Instance.m_effectManager;
    }

    /// <summary>
    /// 효과음 (#478). AppBootstrap 상주라 어느 씬에서도 살아 있지만, 씬을 직접 Play하면
    /// 부트스트랩이 없어 null이다 — 사용처는 ?. 가드 필수.
    /// BGM·UI음이 들어오면(#483) 같은 그룹에 프로퍼티가 늘어난다.
    /// </summary>
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

        // 로컬 HUD — 씬 시작 시점엔 null일 수 있다 (오너 스폰 시 프리팹 생성). 사용처는 ?. 가드 필수
        public static CrosshairUI Crosshair => Instance.m_crosshairUI;
        public static ChannelingGaugeUI Gauge => Instance.m_channelingGaugeUI;
        public static DamageVignetteUI DamageVignette => Instance.m_damageVignetteUI; // 피격 비네트·방향 아크·저체력 글리치 (#476)
        public static TaserShockUI TaserShock => Instance.m_taserShockUI; // 감전 화면 지직 (#477)
        public static ToastView Toast => Instance.m_toastView;
        public static SignalMessageView SignalMessage => Instance.m_signalMessageView;
        public static PromptView Prompt => Instance.m_promptView;
        public static InteractPromptView InteractPrompt => Instance.m_interactPromptView; // 조준 대상 조작키 안내 (#664)
        public static SecretFavorHud SecretFavor => Instance.m_secretFavorHud; // 비밀 청탁 한 줄 (#485)

        // 씬 전환을 덮는 상주 로딩 화면 (AppBootstrap 하위). 씬 직접 Play 등 부트스트랩이 없으면 null
        public static LoadingScreen Loading => Instance.m_loadingScreen;
    }
    #endregion

    // Enter Play Mode Options에서 도메인 리로드를 꺼도 이전 플레이의 매니저 참조가 남지 않도록 리셋
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
