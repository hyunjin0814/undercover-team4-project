using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum RoundPhase
{
    Preparing,
    InProgress,
    Ended
}

public enum RoundResult
{
    None,       // 라운드 종료 전
    Success,    // 목표 달성
    Failure     // 목표 미달
}

public enum RoundEndReason
{
    None,           // 라운드 종료 전
    QuotaMet,       // 목표 금액을 달성한 채로 제한시간 종료 (자동 성공)
    TimeOver,       // 제한시간 초과 + 목표 미달
    AllPlayersDown,
    // SettlementController가 byte로 직렬화하므로 새 사유는 반드시 끝에 붙일 것 — 중간 삽입은 기존 값의 의미를 바꾼다.
    ManualEnd       // 본부 종료 버튼으로 팀이 직접 끝냄 (성공, #395)
}

/// <summary>
/// 라운드 흐름 관리 — 준비(NPC 스폰 → 전원 입장 → 시작 지연) → 진행 중 상태 유지 → 목표 달성/실패 시 종료. (이슈 #42/#103/#403, GDD 3-2)
/// 본 게임의 라운드 진입점.
///
/// 라운드 목표는 검거 수가 아니라 금액이다 (#395). 진행도는 지금 유치장에 잡아둔 대상들의 현상금 합
/// (JailZone.BountyTotal)이며, 목표를 채워도 라운드가 자동으로 끝나지 않는다 — 본부의 종료 버튼이
/// 활성화되고 팀이 원할 때 끝낸다. 더 벌고 싶으면 제한시간까지 계속 수사할 수 있다.
/// 제한시간이 다 됐을 때 목표를 넘겨 있으면 버튼을 누르지 않았어도 성공으로 끝낸다.
///
/// 씬 진입 시 BeginRoundPreparation이 자동으로 돌고, 준비가 끝나야 StartRound로 넘어간다.
/// 스폰을 시작보다 앞에 두는 이유는 로딩 화면이 스폰 완료까지 덮을 수 있게 하기 위함이다 (#403).
///
/// 목표 금액·제한시간 수치는 전부 인스펙터 — 밸런싱 보류 항목(GDD 12장)이라 코드에 못 박지 않는다.
///
/// 범인 배정(CriminalAssigner)·검거 판정(ArrestJudge)이 서버 권위이므로 라운드 진행도 서버(또는 오프라인)에서만 한다.
/// 네트워크 세션에서는 서버만 스폰을 트리거하고 판정을 받는다 — 클라이언트는 관여하지 않는다. (#56 패턴)
/// (타이머도 마찬가지 — Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라 클라에서는 돌지 않는다)
/// </summary>
// TODO: 라운드 페이즈·결과의 클라이언트 동기화는 본부 판정/결과 UI(#43) 연결 시 NetworkVariable/ClientRpc로 추가.
//       (ArrestJudge와 동일 방침 — 지금은 서버 로컬 상태 + 로컬 이벤트로만 둔다)
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class RoundManager : CommonManagerBase
{
    private NpcSpawner Spawner => App.Game.NpcSpawner;
    private ArrestJudge Judge => App.Game.ArrestJudge;
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;

    [Header("라운드 목표")]
    [Tooltip("이번 라운드에 벌어야 하는 목표 금액(#395). 진행도는 유치장에 잡아둔 대상들의 현상금 합이다 — 팀 자금 잔액이 아니다")]
    [Min(1)]
    [SerializeField] private int m_targetFund = 30000;
    [Tooltip("라운드 제한시간(초). 0 이하 = 무제한(타이머 없음)")]
    [SerializeField] private float m_timeLimitSeconds = 180f;

    [Header("라운드 시작 조건")]
    [Tooltip("NPC 스폰 완료 + 전원 입장 확인 후 실제 라운드 시작까지의 대기(초). 0 이하면 즉시 시작")]
    [SerializeField] private float m_startDelaySeconds = 3f;

    [Tooltip("전원 입장 확인을 기다리는 상한(초) — 넘으면 경고 후 남은 인원으로 시작한다")]
    [SerializeField] private float m_peerWaitTimeoutSeconds = 30f;

    [Header("유치장 (비우면 씬에서 자동 탐색)")]
    [Tooltip("목표 진행도(누적 현상금)를 읽어올 유치장. ArrestJudge의 인계 구역 지정과 같은 관례")]
    [SerializeField] private JailZone m_jailZone;

    private NetworkManager m_networkManager;

    // 준비 절차를 한 번만 돌리기 위한 래치
    private bool m_preparing;

    // NGO 씬 동기화가 "이 씬을 전원이 로드했다"고 알려줬는가 (서버에서만 채워진다)
    private bool m_allPeersLoaded;

    // 인스펙터에서 비워 뒀으면 씬에서 한 번 찾아 캐시한다 (ArrestJudge의 인계 구역과 같은 방식).
    // JailZone은 App에 등록된 매니저가 아니라 씬 배치 오브젝트라 App 파사드 경로가 없다.
    private JailZone Jail
    {
        get
        {
            if (m_jailZone == null)
                m_jailZone = FindFirstObjectByType<JailZone>();
            return m_jailZone;
        }
    }

    /// <summary>현재 라운드 단계. 서버(또는 오프라인)의 진실값 — 클라이언트 동기화는 #43에서.</summary>
    public RoundPhase Phase { get; private set; } = RoundPhase.Preparing;

    /// <summary>라운드 종료 결과. 종료 전에는 None.</summary>
    public RoundResult Result { get; private set; } = RoundResult.None;

    /// <summary>라운드 종료 사유 — 종료 피드백 UI(#210)가 읽는다. 종료 전에는 None.</summary>
    public RoundEndReason EndReason { get; private set; } = RoundEndReason.None;

    /// <summary>이번 라운드에 검거한 진범 수 — 할당량 진행도. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public int CriminalArrestCount { get; private set; }

    /// <summary>이번 라운드의 목표 금액 (#395). 씬에 저장된 값이라 모든 피어에서 같다 — 별도 동기화가 필요 없다.</summary>
    public int TargetFund => m_targetFund;

    /// <summary>
    /// 목표 진행도 — 지금 유치장에 잡아둔 대상들의 현상금 합(JailZone.BountyTotal, #395).
    /// 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. 유치장을 못 찾으면 0.
    /// 팀 자금(TeamFund) 잔액과 다르다 — 그쪽은 세션 이월분이라 이번 라운드 성과가 아니다.
    /// </summary>
    public int CurrentFund => Jail != null ? Jail.BountyTotal : 0;

    /// <summary>목표 금액을 채웠는가 — 종료 버튼(#395)의 활성 조건이자 제한시간 종료 시 성공 판정 기준.</summary>
    public bool IsTargetMet => CurrentFund >= m_targetFund;

    /// <summary>남은 제한시간(초). 무제한이면 양의 무한대. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public float RemainingSeconds { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// 라운드 종료로 게임플레이가 정지(freeze)돼야 하는지 — 플레이어 이동(PlayerMovement) 등이 읽는다. (라운드 종료 freeze)
    /// 종료(Ended)이면서 이 피어가 권위(서버/오프라인)일 때만 true.
    /// 원격 클라이언트는 아직 라운드 종료를 동기화받지 못하므로(#43 전) 항상 false를 반환해 오판으로 멈추지 않게 한다.
    /// </summary>
    // TODO(#43): 페이즈 클라 동기화가 붙으면 클라이언트도 종료 시점에 정지하도록 확장한다.
    public bool GameplayFrozen
    {
        get
        {
            if (m_networkManager != null && m_networkManager.IsListening && !m_networkManager.IsServer)
                return false;
            return Phase == RoundPhase.Ended;
        }
    }

    /// <summary>
    /// 라운드 시작 이벤트 — Phase가 InProgress로 넘어가는 순간 발행. UI·연출(#43 등)이 구독한다.
    /// NPC 스폰·범인 배정은 이 시점에 이미 끝나 있다 (준비 단계로 옮김, #403).
    /// </summary>
    public event Action OnRoundStarted;

    /// <summary>라운드 종료 이벤트 — 정산(#42 후속)·결과 UI(#43)·종료 피드백(#210)이 구독한다.</summary>
    public event Action<RoundResult, RoundEndReason> OnRoundEnded;

    private void OnEnable()
    {
        // 전원 다운(전멸) 감시 — 무력화 상태 변화는 서버·오프라인에서만 발행된다. (#105)
        PlayerIncapacitation.OnAnyIncapacitatedChanged += HandleAnyIncapacitatedChanged;
    }

    private void OnDisable()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;

        PlayerIncapacitation.OnAnyIncapacitatedChanged -= HandleAnyIncapacitatedChanged;

        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleCriminalAssigned;
    }

    private void Start()
    {
        // 매니저 간 이벤트 구독 — 모든 매니저의 Awake(App 등록)가 끝난 Start 시점에 한다
        if (Judge != null)
            Judge.OnArrestJudged += HandleArrestJudged;

        // 목표 금액이 배정 현상금 총합을 넘으면 검거만으로는 달성할 수 없다 — 조용히 영구 실패하지
        // 않도록 배정 완료 시점에 대조해 알린다 (#149의 취지를 금액 기준으로 옮긴 것, #395).
        if (Assigner != null)
            Assigner.OnCriminalAssigned += HandleCriminalAssigned;

        if (Spawner == null)
        {
            Debug.LogWarning("RoundManager: NpcSpawner를 찾지 못해 라운드를 시작할 수 없다", this);
            return;
        }

        m_networkManager = NetworkManager.Singleton;

        // 오프라인 실행 — 전원 입장을 기다릴 상대가 없다 (기존 단독 테스트 유지)
        if (m_networkManager == null)
        {
            BeginRoundPreparation();
            return;
        }

        // 네트워크 세션: 게임 씬에 들어옴 = 라운드 준비 시작 신호 (로비는 별도 씬, #214)
        // 서버만 연다(서버 권위) - 클라이언트는 서버의 스폰/판정 동기화만 받음.
        if (!m_networkManager.IsServer)
            return;

        // 전원 로드 완료 신호. 서버의 씬 활성화 프레임(=이 Start)이 NGO의 완료 콜백보다 먼저라 여기서 걸어도 놓치지 않는다.
        if (m_networkManager.SceneManager != null)
            m_networkManager.SceneManager.OnLoadEventCompleted += HandleLoadEventCompleted;

        BeginRoundPreparation();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // ★ 매니저 등록 해제 유지 (R5)

        if (m_networkManager != null && m_networkManager.SceneManager != null)
            m_networkManager.SceneManager.OnLoadEventCompleted -= HandleLoadEventCompleted;
    }

    // NGO 씬 동기화 완료 — 이 씬을 전원이 로드했다. 서버에서만 구독한다.
    private void HandleLoadEventCompleted(
        string sceneName,
        LoadSceneMode mode,
        List<ulong> clientsCompleted,
        List<ulong> clientsTimedOut
    )
    {
        if (sceneName != gameObject.scene.name)
            return;

        // 시간 초과 클라이언트가 있어도 진행한다 — NGO가 이미 자체 상한을 적용한 뒤이고, 여기서 더 기다려도 안 온다.
        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
            Debug.LogWarning($"[라운드] 씬 로드 시간 초과 {clientsTimedOut.Count}명 — 남은 인원으로 진행한다", this);

        m_allPeersLoaded = true;
    }

    // 서버 재시작 시 이전 라운드 상태를 초기화한다 — Phase·결과·진행도와 스포너 래치를 되돌려 재스폰을 허용한다.
    private void ResetForRestart()
    {
        Phase = RoundPhase.Preparing;
        Result = RoundResult.None;
        EndReason = RoundEndReason.None;
        CriminalArrestCount = 0;
        RemainingSeconds = float.PositiveInfinity;
        m_preparing = false;
        m_allPeersLoaded = false;
        Spawner.ResetSpawnState(); // IsSpawnCompleted 래치 해제 + 이전 NPC 정리 → StartSpawn 재동작
    }

    /// <summary>
    /// 라운드 준비를 시작한다 — NPC를 먼저 스폰하고, 스폰 완료 + 전원 입장 확인 후 지연을 두고 StartRound로 넘어간다.
    /// 게임 씬 진입 시 서버(또는 오프라인)에서 자동 호출된다.
    /// 씬을 직접 Play하는 개발 흐름에서는 호스트를 띄운 뒤 DevAutoHost가 직접 호출한다.
    /// Phase는 이 구간 내내 Preparing이다 — 타이머·검거 판정은 StartRound부터 돈다.
    /// </summary>
    public void BeginRoundPreparation()
    {
        if (m_preparing || Phase != RoundPhase.Preparing)
            return;

        if (Spawner == null)
        {
            Debug.LogWarning("RoundManager: NpcSpawner를 찾지 못해 라운드를 준비할 수 없다", this);
            return;
        }

        m_preparing = true;

        Spawner.StartSpawn(); // 서버/오프라인만 실제 스폰 — 클라이언트 호출은 NpcSpawner가 걸러낸다 (#56)
        PrepareAndStartAsync().Forget();
    }

    private async UniTaskVoid PrepareAndStartAsync()
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        // 1. NPC 스폰 완료 — NpcSpawner는 프레임당 한 마리씩 스폰한다. 이 대기 중에 범인 배정도 이어져 끝난다.
        await UniTask.WaitUntil(
            () => Spawner == null || Spawner.IsSpawnCompleted,
            cancellationToken: token
        );

        // 2. 전원 입장 확인 — 기다릴 상대가 실제로 있을 때만. 호스트 혼자면(솔로 플레이, DevAutoHost로 씬을
        //    직접 Play하는 개발 흐름) NGO 씬 동기화 자체가 없어 완료 신호가 영영 오지 않으므로 여기서 걸러낸다.
        //    정식 흐름에서는 클라가 Title/Lobby에서 이미 접속해 있어 이 시점에 ConnectedClients에 들어와 있다.
        bool waitForPeers =
            m_networkManager != null
            && m_networkManager.IsListening
            && m_networkManager.ConnectedClientsIds.Count > 1;

        if (waitForPeers && !m_allPeersLoaded)
        {
            float deadline = Time.realtimeSinceStartup + m_peerWaitTimeoutSeconds;
            await UniTask.WaitUntil(
                () =>
                    m_allPeersLoaded
                    || !m_networkManager.IsListening
                    || Time.realtimeSinceStartup >= deadline,
                cancellationToken: token
            );

            if (!m_allPeersLoaded)
                Debug.LogWarning(
                    $"[라운드] 전원 입장 확인을 {m_peerWaitTimeoutSeconds}초 내에 받지 못했다 — 그대로 시작한다",
                    this
                );
        }

        // 3. 시작 지연 — 라운드 종료 freeze 등으로 timeScale이 건드려져도 흐르도록 실시간 기준 (RoundEndResetter와 동일 방침)
        if (m_startDelaySeconds > 0f)
        {
            Debug.Log($"[라운드] 준비 완료 — {m_startDelaySeconds:0.#}초 후 시작");
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_startDelaySeconds),
                ignoreTimeScale: true,
                cancellationToken: token
            );
        }

        StartRound();
    }

    /// <summary>
    /// 라운드를 진행 상태로 전환한다 — 제한시간이 여기서부터 흐른다.
    /// NPC 스폰·범인 배정은 BeginRoundPreparation에서 이미 끝나 있다.
    /// </summary>
    public void StartRound()
    {
        if (Phase != RoundPhase.Preparing)
            return;

        Phase = RoundPhase.InProgress;
        CriminalArrestCount = 0;
        // 0 이하 = 무제한 — 타이머를 아예 돌리지 않는다 (밸런싱 전 테스트·본부 단독 씬용)
        RemainingSeconds = m_timeLimitSeconds > 0f ? m_timeLimitSeconds : float.PositiveInfinity;
        Debug.Log($"[라운드] 시작 — 목표 {m_targetFund}원, 제한시간 {(float.IsPositiveInfinity(RemainingSeconds) ? "무제한" : $"{RemainingSeconds:0}초")}");
        OnRoundStarted?.Invoke();
    }

    // 배정 완료 시점에 목표 금액이 달성 가능한지 대조한다 (#395).
    // 값을 강제로 깎지 않는 이유: 돌발 이벤트(난동꾼·침입자) 수익이 나중에 더해질 수 있어 여기서 본 총합이
    // 라운드 최대치가 아니다. 임의로 목표를 낮추면 밸런싱 의도를 코드가 덮어쓰게 되므로 경고만 남긴다.
    private void HandleCriminalAssigned(IReadOnlyList<NpcController> criminals)
    {
        if (Assigner == null)
            return;

        int assigned = Assigner.TotalAssignedBounty;
        if (m_targetFund <= assigned)
            return;

        Debug.LogWarning(
            $"RoundManager: 목표 금액({m_targetFund}원)이 이번 라운드 배정 현상금 총합({assigned}원)보다 큽니다 — "
                + "검거만으로는 달성 불가입니다. 돌발 이벤트 수익으로 메워야 하니 목표 금액이나 현상금 범위를 조정할 것.",
            this
        );
    }

    private void Update()
    {
        // 제한시간 진행 (#103). Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라
        // 클라이언트에서는 이 타이머가 돌지 않는다 — 라운드 진행은 서버 권위.
        if (Phase != RoundPhase.InProgress || float.IsPositiveInfinity(RemainingSeconds))
            return;

        RemainingSeconds -= Time.deltaTime;
        if (RemainingSeconds > 0f)
            return;
        RemainingSeconds = 0f;

        // 목표를 채웠는데 종료 버튼을 누르지 않은 채 시간이 다 됐다면 성공으로 끝낸다 (#395) —
        // 버튼은 일찍 끊고 안전하게 챙기는 수단이지, 누르는 걸 잊으면 지는 함정이 아니다.
        // (전원 다운(전멸)은 목표 달성 여부와 무관하게 실패다 — GDD 9-3, HandleAnyIncapacitatedChanged, #105)
        if (IsTargetMet)
        {
            Debug.Log($"[라운드] 제한시간 종료 — 목표 달성 상태({CurrentFund}/{m_targetFund}원)로 성공 처리");
            EndRound(RoundResult.Success, RoundEndReason.QuotaMet);
            return;
        }

        Debug.Log($"[라운드] 제한시간 초과 — {CurrentFund}/{m_targetFund}원, 목표 미달");
        EndRound(RoundResult.Failure, RoundEndReason.TimeOver);
    }

    // 검거 판정 결과 수신 — 진범 검거를 할당량에 누적하고, 채우면 성공 종료. (서버/오프라인에서만 발행됨, ArrestJudge)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (Phase != RoundPhase.InProgress)
            return;

        // 진범 검거만 할당량에 누적 — 오검거는 라운드를 끝내지도, 할당량을 채우지도 않는다 (GDD 9-3).
        // 오검거 정산·페널티는 팀 자금 정산 이슈(별도)가 이 이벤트를 따로 구독해 처리한다.
        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        // 재판정(같은 진범을 다시 인계)으로 할당량이 부풀지 않게 — 첫 인계에만 누적한다.
        // 탈옥(ClearDelivered)하면 다시 첫 인계가 되어 재검거 때 정상적으로 재누적된다. (#358)
        if (!result.IsFirstDelivery)
            return;

        CriminalArrestCount++;
        // 라운드는 여기서 끝나지 않는다 (#395) — 목표는 금액이고, 종료 시점은 본부의 종료 버튼이 쥔다.
        // 현상금은 CustodyRouter가 유치장에 넘겨 JailZone.BountyTotal로 반영된다.
        Debug.Log($"[라운드] 진범 검거 {CriminalArrestCount}명 — 목표 진행 {CurrentFund}/{m_targetFund}원");
    }

    /// <summary>
    /// 본부 종료 버튼으로 라운드를 끝낸다 — 성공 종료. (#395)
    /// 서버(또는 오프라인)에서만 호출된다. 버튼의 활성 여부는 클라이언트 표시용 게이팅일 뿐이므로
    /// 여기서 목표 달성을 다시 검증한다 — 조작된 RPC로 미달 상태에서 끝낼 수 없게.
    /// </summary>
    /// <returns>실제로 종료했으면 true. 진행 중이 아니거나 목표 미달이면 false.</returns>
    public bool TryEndRoundManually()
    {
        if (Phase != RoundPhase.InProgress)
            return false;

        if (!IsTargetMet)
        {
            Debug.LogWarning($"RoundManager: 목표 미달({CurrentFund}/{m_targetFund}원) 상태의 종료 요청을 거부했다", this);
            return false;
        }

        Debug.Log($"[라운드] 본부 종료 버튼 — {CurrentFund}/{m_targetFund}원으로 라운드를 마친다");
        EndRound(RoundResult.Success, RoundEndReason.ManualEnd);
        return true;
    }

    /// <summary>
    /// 검거했던 진범이 유치장에서 탈출했다 — 검거 수 집계에서 다시 뺀다. (GDD 6-4, #231)
    /// 목표 진행도(금액)는 JailZone.ReleaseInmate가 레코드를 지우면서 스스로 줄어들므로 여기서 손대지 않는다 (#395).
    /// 서버(또는 오프라인)에서만 호출된다(범인 탈출 이벤트가 서버 권위).
    ///
    /// 팀 자금은 여기서 직접 손대지 않는다 — 보상 정산이 라운드 종료 시 유치장 점유 기반으로 바뀌면서(#340,
    /// 2026-07-23 '탈옥 시 자금 미회수' 스펙 대체), 탈옥해 유치장에 없는 진범은 종료 정산에서 자연히 빠진다.
    /// 여기서 할당량만 되돌리면 되고, "다시 잡아야 한다"는 압박은 할당량 + 정산 누락으로 함께 만들어진다.
    /// </summary>
    public void ReportCriminalEscaped()
    {
        if (Phase != RoundPhase.InProgress)
            return;

        // 할당량을 채우는 순간 라운드가 성공 종료되므로 InProgress 중에는 0 미만이 될 수 없지만,
        // 호출 경로가 늘어도 진행도가 음수로 새지 않게 방어한다
        if (CriminalArrestCount <= 0)
            return;

        CriminalArrestCount--;
        Debug.Log($"[라운드] 진범 탈출 — 진범 검거 {CriminalArrestCount}명, 목표 진행 {CurrentFund}/{m_targetFund}원");
    }

    // 플레이어 무력화 상태 변화 수신 — 전원 다운(전멸)이면 게임오버로 종료한다. (#105, 서버/오프라인에서만 발행됨)
    // Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라 클라이언트에서는 아래 가드에 걸려 아무 일도 하지 않는다.
    private void HandleAnyIncapacitatedChanged()
    {
        if (Phase != RoundPhase.InProgress)
            return;
        if (!AreAllPlayersOutOfAction())
            return;

        Debug.Log("[라운드] 플레이어 전원 행동불능(다운/기능 정지) — 전멸(게임오버)");
        EndRound(RoundResult.Failure, RoundEndReason.AllPlayersDown);
    }

    // 현재 존재하는 모든 플레이어가 다운 또는 Die인지 — 한 명이라도 멀쩡하면 false. 플레이어가 없으면 전멸이 아니다.
    // 무력화 전부가 아니라 IsOutOfAction만 센다 (#252/#364) — 곧 스스로 일어나는 기절·매달기를 세면 아무도
    // 잃지 않았는데 게임오버가 뜨고, 반대로 다운만 세면 전원이 Die로 넘어간 순간 판정이 통과하지 못해
    // 게임오버가 영영 안 뜬다. (IsOutOfAction이 온라인=동기화값/서버=실참조, 오프라인=실참조를 알아서 처리한다)
    private static bool AreAllPlayersOutOfAction()
    {
        // 씬 조회(FindObjectsByType) 대신 정적 레지스트리 — 인스턴스가 스스로 등록·해제한다 (#365)
        System.Collections.Generic.IReadOnlyList<PlayerIncapacitation> players = PlayerIncapacitation.All;
        if (players.Count == 0)
            return false;

        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].IsOutOfAction)
                return false;
        }
        return true;
    }

    /// <summary>라운드를 종료한다 — 성공(할당량 달성)·실패(제한시간 초과/전멸) 공통 경로. (#42/#103)</summary>
    public void EndRound(RoundResult result, RoundEndReason reason)
    {
        if (Phase == RoundPhase.Ended)
            return;

        Phase = RoundPhase.Ended;
        Result = result;
        EndReason = reason;
        FreezeAllNpcs(); // NPC 정지 — 플레이어 정지는 PlayerMovement가 GameplayFrozen을 읽어 처리
        Debug.Log($"[라운드] 종료 — 결과: {result} (사유: {reason})");
        OnRoundEnded?.Invoke(result, reason);
    }

    // 스폰된 NPC를 전부 정지시킨다 — 서버(또는 오프라인)에서만 호출되며, 서버 정지가 전 클라이언트로 복제된다.
    private void FreezeAllNpcs()
    {
        if (Spawner == null)
            return;

        foreach (NpcController npc in Spawner.SpawnedNpcs)
        {
            if (npc != null)
                npc.SetFrozen(true);
        }
    }
}
