using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

public enum RoundPhase
{
    Preparing,
    InProgress,
    Ended
}

// 값 이름이 곧 정산 화면 문구의 키다 — 결과 제목(Settlement.Result.)과 복귀 도착지(Settlement.Return.,
// 성공=상점 / 실패=로비) 둘 다. 값을 추가하면 SettlementTable에 같은 이름의 키를 함께 넣을 것.
[LocalizedEnum("SettlementTable", "Settlement.Result.", nameof(RoundResult.None))]
[LocalizedEnum("SettlementTable", "Settlement.Return.", nameof(RoundResult.None))]
public enum RoundResult
{
    None,       // 라운드 종료 전
    Success,    // 목표 달성
    Failure     // 목표 미달
}

// 값 이름이 곧 종료 사유 문구의 키다 (Settlement.Reason. + 이름). None은 종료 전이라 표시 대상이 아니다.
[LocalizedEnum("SettlementTable", "Settlement.Reason.", nameof(RoundEndReason.None))]
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
    [Tooltip("라운드가 지날수록 오르는 할당량 표(#377). 비우면 위의 목표 금액을 그대로 쓴다")]
    [SerializeField] private RoundQuotaTable m_quotaTable;
    [Tooltip("라운드 제한시간(초). 기준값은 600(10분) — GDD 3-2. 0 이하 = 무제한(타이머 없음)")]
    [SerializeField] private float m_timeLimitSeconds = 600f;

    [Header("라운드 시작 조건")]
    [Tooltip("NPC 스폰 완료 + 전원 입장 확인 후 실제 라운드 시작까지의 대기(초). 0 이하면 즉시 시작")]
    [SerializeField] private float m_startDelaySeconds = 3f;

    [Header("유치장 (비우면 씬에서 자동 탐색)")]
    [Tooltip("목표 진행도(누적 현상금)를 읽어올 유치장. ArrestJudge의 인계 구역 지정과 같은 관례")]
    [SerializeField] private JailZone m_jailZone;

    private NetworkManager m_networkManager;

    // 준비 절차를 한 번만 돌리기 위한 래치
    private bool m_preparing;

    // 라운드 종료 시점의 목표 금액 스냅샷 (#377). 종료 직후 진행도가 오르면(RoundEndResetter) 표 조회
    // 결과가 다음 라운드 값으로 바뀐다 — 정산(SettlementController)은 이번 라운드 값을 봐야 하므로 얼려 둔다.
    // 음수 = 아직 종료 전(표를 그대로 조회한다).
    private int m_endedTargetFund = -1;

    // 인스펙터에서 비워 뒀으면 씬에서 한 번 찾아 캐시한다 (ArrestJudge의 인계 구역과 같은 방식).
    // 감옥이 없는 씬(로비·타이틀)에서는 null이다 — 읽는 쪽이 전부 null을 검사한다 (#592).
    private JailZone Jail
    {
        get
        {
            if (m_jailZone == null)
                m_jailZone = App.Game.Jail;
            return m_jailZone;
        }
    }

    // 내부에서는 이 필드를 직접 읽는다 — Update처럼 클라에서도 도는 경로가 있어 프로퍼티로 읽으면 경고가 매 프레임 뜬다.
    private RoundPhase m_phase = RoundPhase.Preparing;

    // 클라 접근 경고를 이미 남긴 읽기 지점 — "읽은 메서드마다 1회"로 거른다.
    // 단일 플래그로 두면 스폰 직전 구간에 정상적으로 한 번 읽는 쪽(DoubleDoor·RoundEndButton·
    // SuddenEventManager·TipCallPhone이 권위를 !IsSpawned로 판단한다)이 그 1회를 먹어, 정작 잘못 읽는
    // 새 코드가 조용해진다 — 가드가 존재하는 이유가 사라지는 것이라 지점별로 나눠 센다.
    private readonly HashSet<string> m_warnedPhaseReaders = new HashSet<string>();

    /// <summary>
    /// 이 피어가 라운드 진행의 권위(서버 또는 오프라인)인가 — <see cref="Phase"/>를 읽어도 되는 피어인지의 기준.
    /// 캐시(m_networkManager)가 아니라 싱글턴을 직접 본다 — 캐시는 이 매니저의 Start에서 채워지므로,
    /// 그 전에 도는 남의 Awake·OnNetworkSpawn에서는 클라가 권위로 오판된다(가장 이른 코드가 가장 안 잡힌다).
    /// </summary>
    public bool IsPhaseAuthority
    {
        get
        {
            NetworkManager net = NetworkManager.Singleton;
            return net == null || !net.IsListening || net.IsServer;
        }
    }

    /// <summary>
    /// 현재 라운드 단계. <b>서버(또는 오프라인) 전용 상태다</b> — 대입 지점이 전부 서버 경로라
    /// 클라이언트에서는 영원히 Preparing이다. 동기화가 빠진 게 아니라, 클라가 필요한 값은 각자 따로
    /// 동기화받는 설계다(남은 시간은 RoundTimerSync, 종료 버튼 활성은 RoundEndButton, 진행도는 JailZone.BountyTotal).
    /// 클라에서 읽으면 경고를 남긴다 — 새 코드가 여기에 기대면 컴파일도 되고 예외도 없이 조용히 틀리기 때문이다.
    /// </summary>
    public RoundPhase Phase
    {
        get
        {
            if (!IsPhaseAuthority)
                WarnClientPhaseRead();
            return m_phase;
        }
    }

    // 클라가 Phase를 읽었다 — 읽은 지점마다 1회씩 알린다. 스택 조회는 이 경로에서만 도는데,
    // 스폰이 끝나면 사용처들이 자기 권위 가드에서 먼저 끊겨 여기까지 오지 않으므로 접속 초반 몇 번뿐이다.
    private void WarnClientPhaseRead()
    {
        // 프레임 2 = 프로퍼티 getter를 부른 쪽. 인라이닝으로 어긋날 수 있어 이름은 참고용으로만 쓴다.
        System.Reflection.MethodBase reader = new System.Diagnostics.StackTrace(2, false)
            .GetFrame(0)
            ?.GetMethod();
        string where =
            reader != null ? $"{reader.DeclaringType?.Name}.{reader.Name}" : "알 수 없는 지점";

        if (!m_warnedPhaseReaders.Add(where))
            return;

        Debug.LogWarning(
            $"[라운드] Phase는 서버 전용 상태다 — 클라는 동기화된 값을 쓸 것 (읽은 곳: {where})",
            this
        );
    }

    /// <summary>라운드 종료 결과. 종료 전에는 None.</summary>
    public RoundResult Result { get; private set; } = RoundResult.None;

    /// <summary>라운드 종료 사유 — 종료 피드백 UI(#210)가 읽는다. 종료 전에는 None.</summary>
    public RoundEndReason EndReason { get; private set; } = RoundEndReason.None;

    /// <summary>이번 라운드에 검거한 진범 수 — 할당량 진행도. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public int CriminalArrestCount { get; private set; }

    /// <summary>
    /// 이번 라운드의 목표 금액(할당량) (#395). 라운드가 지날수록 오른다 (#377) — 표(m_quotaTable)에서
    /// 현재 라운드(App.Game.RoundProgress)의 행을 읽는다. 표를 안 붙였으면 인스펙터 목표 금액 그대로다.
    /// 진행도는 NetworkVariable, 표는 씬에 저장된 에셋이라 모든 피어가 같은 값을 계산한다 — 별도 동기화가 필요 없다.
    /// 상주 진행도가 없는 경우(오프라인 단독 Play·테스트 씬)는 1라운드로 취급한다.
    /// </summary>
    public int TargetFund
    {
        get
        {
            // 종료 뒤에는 얼려 둔 값을 준다 — 정산·결과 UI가 다음 라운드 목표를 읽지 않게
            if (m_endedTargetFund >= 0) return m_endedTargetFund;

            if (m_quotaTable == null) return m_targetFund;

            RoundProgress progress = App.Game.RoundProgress;
            int round = progress != null ? progress.Current : RoundProgress.k_firstRound;
            return m_quotaTable.GetQuota(round, m_targetFund);
        }
    }

    /// <summary>
    /// 목표 진행도 — 지금 유치장에 잡아둔 대상들의 현상금 합(JailZone.BountyTotal, #395).
    /// 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다. 유치장을 못 찾으면 0.
    /// 팀 자금(TeamFund) 잔액과 다르다 — 그쪽은 세션 이월분이라 이번 라운드 성과가 아니다.
    /// </summary>
    public int CurrentFund => Jail != null ? Jail.BountyTotal : 0;

    /// <summary>목표 금액을 채웠는가 — 종료 버튼(#395)의 활성 조건이자 제한시간 종료 시 성공 판정 기준.</summary>
    public bool IsTargetMet => CurrentFund >= TargetFund;

    /// <summary>남은 제한시간(초). 무제한이면 양의 무한대. 서버(또는 오프라인)의 진실값. (#103)</summary>
    public float RemainingSeconds { get; private set; } = float.PositiveInfinity;

    /// <summary>
    /// 라운드 종료로 게임플레이가 정지(freeze)돼야 하는지 — 플레이어 이동(PlayerMovement) 등이 읽는다. (라운드 종료 freeze)
    /// 종료(Ended)이면서 이 피어가 권위(서버/오프라인)일 때만 true.
    /// 원격 클라이언트는 Phase를 받지 못하므로 항상 false — 오판으로 멈추지 않게 한다.
    /// </summary>
    public bool GameplayFrozen => IsPhaseAuthority && m_phase == RoundPhase.Ended;

    /// <summary>
    /// 라운드 시작 이벤트 — Phase가 InProgress로 넘어가는 순간 발행. UI·연출이 구독한다.
    /// NPC 스폰·범인 배정은 이 시점에 이미 끝나 있다 (준비 단계로 옮김, #403).
    /// </summary>
    public event Action OnRoundStarted;

    /// <summary>라운드 종료 이벤트 — 정산(#42 후속)·종료 피드백(#210)이 구독한다.</summary>
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

        BeginRoundPreparation();
    }

    // 서버 재시작 시 이전 라운드 상태를 초기화한다 — Phase·결과·진행도와 스포너 래치를 되돌려 재스폰을 허용한다.
    private void ResetForRestart()
    {
        m_phase = RoundPhase.Preparing;
        Result = RoundResult.None;
        EndReason = RoundEndReason.None;
        CriminalArrestCount = 0;
        RemainingSeconds = float.PositiveInfinity;
        m_endedTargetFund = -1;
        m_preparing = false;
        m_warnedPhaseReaders.Clear();
        Spawner.ResetSpawnState(); // IsSpawnCompleted 래치 해제 + 이전 NPC 정리 → StartSpawn 재동작
    }

    /// <summary>
    /// 라운드 준비를 시작한다 — NPC를 먼저 스폰하고, 스폰 완료 + 전원 입장 확인 후 지연을 두고 StartRound로 넘어간다.
    /// 게임 씬 진입 시 서버(또는 오프라인)에서 자동 호출된다.
    /// 씬을 직접 Play하는 개발 흐름에서는 호스트를 띄운 뒤 DevAutoHost가 직접 호출한다.
    /// Phase는 이 구간 내내 Preparing이다 — 타이머·검거 판정은 StartRound부터 돈다.
    /// NPC도 정지 상태로 스폰해 둔다 — 먼저 입장한 플레이어만 초반 동선을 보는 것을 막는다.
    /// </summary>
    public void BeginRoundPreparation()
    {
        if (m_preparing || m_phase != RoundPhase.Preparing)
            return;

        if (Spawner == null)
        {
            Debug.LogWarning("RoundManager: NpcSpawner를 찾지 못해 라운드를 준비할 수 없다", this);
            return;
        }

        m_preparing = true;

        // 서버/오프라인만 실제 스폰 — 클라이언트 호출은 NpcSpawner가 걸러낸다 (#56)
        Spawner.StartSpawn(spawnFrozen: true);
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

        // 2. 전원 준비 완료 — 각 피어가 "내 화면이 실제로 준비됐다"를 보고하고 서버가 판정한다 (SceneReadyGate, #410).
        //    씬 로드 완료(OnLoadEventCompleted)로 판정하던 것을 바꾼 것이다 — 그건 서버 관점이라, 클라에
        //    아직 남아 있는 배치 복제 수신·첫 렌더·로딩 화면 페이드를 못 본다.
        //    기다릴 상대가 실제로 있을 때만 본다. 호스트 혼자면(솔로 플레이, DevAutoHost로 씬을 직접
        //    Play하는 개발 흐름) 보고할 클라가 없어 게이트가 타임아웃으로만 열리므로 여기서 걸러낸다.
        bool waitForPeers =
            m_networkManager != null
            && m_networkManager.IsListening
            && m_networkManager.ConnectedClientsIds.Count > 1;

        if (waitForPeers)
        {
            SceneReadyGate gate = App.Game.ReadyGate;
            if (gate == null)
            {
                Debug.LogWarning("[RoundManager] SceneReadyGate를 찾지 못해 전원 준비 완료를 확인할 수 없다 - 그대로 시작.",
                    this
                );
            }
            else
            {
                // 타임아웃은 게이트가 쥔다 — 여기서 따로 재면 두 카운트다운이 어긋나 로딩 화면과 라운드
                // 시작 시점이 벌어진다. 세션이 도중에 끊기면 열릴 일이 없으므로 IsListening도 종료 조건에 넣는다.
                await UniTask.WaitUntil(
                    () => gate == null || gate.IsOpen || !m_networkManager.IsListening,
                    cancellationToken: token
                );
            }
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
        if (m_phase != RoundPhase.Preparing)
            return;

        m_phase = RoundPhase.InProgress;
        CriminalArrestCount = 0;
        SetNpcsFrozen(false); // 준비 중 정지시켜 둔 NPC를 풀어 준다 — 세계는 여기서부터 움직인다
        // 0 이하 = 무제한 — 타이머를 아예 돌리지 않는다 (밸런싱 전 테스트·본부 단독 씬용)
        RemainingSeconds = m_timeLimitSeconds > 0f ? m_timeLimitSeconds : float.PositiveInfinity;
        Debug.Log($"[라운드] 시작 — 목표 {TargetFund}원, 제한시간 {(float.IsPositiveInfinity(RemainingSeconds) ? "무제한" : $"{RemainingSeconds:0}초")}");
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
        if (TargetFund <= assigned)
            return;

        Debug.LogWarning(
            $"RoundManager: 목표 금액({TargetFund}원)이 이번 라운드 배정 현상금 총합({assigned}원)보다 큽니다 — "
                + "검거만으로는 달성 불가입니다. 돌발 이벤트 수익으로 메워야 하니 목표 금액이나 현상금 범위를 조정할 것.",
            this
        );
    }

    private void Update()
    {
        // 제한시간 진행 (#103). Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라
        // 클라이언트에서는 이 타이머가 돌지 않는다 — 라운드 진행은 서버 권위.
        if (m_phase != RoundPhase.InProgress || float.IsPositiveInfinity(RemainingSeconds))
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
            Debug.Log($"[라운드] 제한시간 종료 — 목표 달성 상태({CurrentFund}/{TargetFund}원)로 성공 처리");
            EndRound(RoundResult.Success, RoundEndReason.QuotaMet);
            return;
        }

        Debug.Log($"[라운드] 제한시간 초과 — {CurrentFund}/{TargetFund}원, 목표 미달");
        EndRound(RoundResult.Failure, RoundEndReason.TimeOver);
    }

    // 검거 판정 결과 수신 — 진범 검거를 할당량에 누적하고, 채우면 성공 종료. (서버/오프라인에서만 발행됨, ArrestJudge)
    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_phase != RoundPhase.InProgress)
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
        Debug.Log($"[라운드] 진범 검거 {CriminalArrestCount}명 — 목표 진행 {CurrentFund}/{TargetFund}원");
    }

    /// <summary>
    /// 본부 종료 버튼으로 라운드를 끝낸다 — 성공 종료. (#395)
    /// 서버(또는 오프라인)에서만 호출된다. 버튼의 활성 여부는 클라이언트 표시용 게이팅일 뿐이므로
    /// 여기서 목표 달성을 다시 검증한다 — 조작된 RPC로 미달 상태에서 끝낼 수 없게.
    /// </summary>
    /// <returns>실제로 종료했으면 true. 진행 중이 아니거나 목표 미달이면 false.</returns>
    public bool TryEndRoundManually()
    {
        if (m_phase != RoundPhase.InProgress)
            return false;

        if (!IsTargetMet)
        {
            Debug.LogWarning($"RoundManager: 목표 미달({CurrentFund}/{TargetFund}원) 상태의 종료 요청을 거부했다", this);
            return false;
        }

        Debug.Log($"[라운드] 본부 종료 버튼 — {CurrentFund}/{TargetFund}원으로 라운드를 마친다");
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
        if (m_phase != RoundPhase.InProgress)
            return;

        // 할당량을 채우는 순간 라운드가 성공 종료되므로 InProgress 중에는 0 미만이 될 수 없지만,
        // 호출 경로가 늘어도 진행도가 음수로 새지 않게 방어한다
        if (CriminalArrestCount <= 0)
            return;

        CriminalArrestCount--;
        Debug.Log($"[라운드] 진범 탈출 — 진범 검거 {CriminalArrestCount}명, 목표 진행 {CurrentFund}/{TargetFund}원");
    }

    // 플레이어 무력화 상태 변화 수신 — 전원 다운(전멸)이면 게임오버로 종료한다. (#105, 서버/오프라인에서만 발행됨)
    // Phase가 InProgress가 되는 곳이 서버/오프라인뿐이라 클라이언트에서는 아래 가드에 걸려 아무 일도 하지 않는다.
    private void HandleAnyIncapacitatedChanged()
    {
        if (m_phase != RoundPhase.InProgress)
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
        if (m_phase == RoundPhase.Ended)
            return;

        m_phase = RoundPhase.Ended;
        Result = result;
        EndReason = reason;
        m_endedTargetFund = TargetFund; // 구독자(정산)가 읽기 전에 이번 라운드 값으로 고정 (#377)
        SetNpcsFrozen(true); // NPC 정지 — 플레이어 정지는 PlayerMovement가 GameplayFrozen을 읽어 처리
        Debug.Log($"[라운드] 종료 — 결과: {result} (사유: {reason})");
        OnRoundEnded?.Invoke(result, reason);
    }

    // 스폰된 NPC를 전부 정지/재개시킨다 — 서버(또는 오프라인)에서만 호출되며, 서버 정지가 전 클라이언트로 복제된다.
    // 준비 중 정지(StartRound에서 해제)와 라운드 종료 정지가 같은 경로를 쓴다.
    private void SetNpcsFrozen(bool frozen)
    {
        if (Spawner == null)
            return;

        foreach (NpcController npc in Spawner.SpawnedNpcs)
        {
            if (npc != null)
                npc.SetFrozen(frozen);
        }
    }
}
