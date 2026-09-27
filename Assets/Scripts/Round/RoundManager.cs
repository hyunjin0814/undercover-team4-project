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

[LocalizedEnum("SettlementTable", "Settlement.Result.", nameof(RoundResult.None))]
[LocalizedEnum("SettlementTable", "Settlement.Return.", nameof(RoundResult.None))]
public enum RoundResult
{
    None,
    Success,
    Failure
}

[LocalizedEnum("SettlementTable", "Settlement.Reason.", nameof(RoundEndReason.None))]
public enum RoundEndReason
{
    None,
    QuotaMet,
    TimeOver,
    AllPlayersDown,
    ManualEnd
}

/// <summary>
/// 라운드 흐름 관리 — 준비(NPC 스폰·전원 입장) → 진행 → 종료(GDD 3-2). 서버(또는 오프라인) 전용.
/// 목표는 유치장 현상금 합이며, 채우면 본부 종료 버튼이 활성화되고 제한시간 종료 시 달성 여부로 성패를 가른다.
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

    private bool m_preparing;

    private int m_endedTargetFund = -1;

    private JailZone Jail
    {
        get
        {
            if (m_jailZone == null)
                m_jailZone = App.Game.Jail;
            return m_jailZone;
        }
    }

    private RoundPhase m_phase = RoundPhase.Preparing;

    private readonly HashSet<string> m_warnedPhaseReaders = new HashSet<string>();

    public bool IsPhaseAuthority
    {
        get
        {
            NetworkManager net = NetworkManager.Singleton;
            return net == null || !net.IsListening || net.IsServer;
        }
    }

    public RoundPhase Phase
    {
        get
        {
            if (!IsPhaseAuthority)
                WarnClientPhaseRead();
            return m_phase;
        }
    }

    private void WarnClientPhaseRead()
    {
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

    public RoundResult Result { get; private set; } = RoundResult.None;

    public RoundEndReason EndReason { get; private set; } = RoundEndReason.None;

    public int CriminalArrestCount { get; private set; }

    public int TargetFund
    {
        get
        {
            if (m_endedTargetFund >= 0) return m_endedTargetFund;

            if (m_quotaTable == null) return m_targetFund;

            RoundProgress progress = App.Game.RoundProgress;
            int round = progress != null ? progress.Current : RoundProgress.k_firstRound;
            return m_quotaTable.GetQuota(round, m_targetFund);
        }
    }

    public int CurrentFund => Jail != null ? Jail.BountyTotal : 0;

    public bool IsTargetMet => CurrentFund >= TargetFund;

    public float RemainingSeconds { get; private set; } = float.PositiveInfinity;

    public bool GameplayFrozen => IsPhaseAuthority && m_phase == RoundPhase.Ended;

    public event Action OnRoundStarted;

    public event Action<RoundResult, RoundEndReason> OnRoundEnded;

    private void OnEnable()
    {
        PlayerIncapacitation.OnAnyIncapacitatedChanged += HandleAnyIncapacitatedChanged;
    }

    private void OnDisable()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;

        PlayerIncapacitation.OnAnyIncapacitatedChanged -= HandleAnyIncapacitatedChanged;

        if (m_networkManager != null)
            m_networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;

        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleCriminalAssigned;
    }

    private void Start()
    {
        if (Judge != null)
            Judge.OnArrestJudged += HandleArrestJudged;

        if (Assigner != null)
            Assigner.OnCriminalAssigned += HandleCriminalAssigned;

        if (Spawner == null)
        {
            Debug.LogWarning("RoundManager: NpcSpawner를 찾지 못해 라운드를 시작할 수 없다", this);
            return;
        }

        m_networkManager = NetworkManager.Singleton;

        if (m_networkManager == null)
        {
            BeginRoundPreparation();
            return;
        }

        if (!m_networkManager.IsServer)
            return;

        m_networkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        BeginRoundPreparation();
    }

    /// <summary>NPC를 정지 상태로 스폰하고, 스폰 완료와 전원 입장 후 지연을 두고 StartRound로 넘어간다.</summary>
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

        Spawner.StartSpawn(spawnFrozen: true);
        PrepareAndStartAsync().Forget();
    }

    private async UniTaskVoid PrepareAndStartAsync()
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        await UniTask.WaitUntil(
            () => Spawner == null || Spawner.IsSpawnCompleted,
            cancellationToken: token
        );

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
                await UniTask.WaitUntil(
                    () => gate == null || gate.IsOpen || !m_networkManager.IsListening,
                    cancellationToken: token
                );
            }
        }

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

    /// <summary>라운드를 진행 상태로 전환하고 제한시간을 시작한다.</summary>
    public void StartRound()
    {
        if (m_phase != RoundPhase.Preparing)
            return;

        m_phase = RoundPhase.InProgress;
        CriminalArrestCount = 0;
        SetNpcsFrozen(false);
        RemainingSeconds = m_timeLimitSeconds > 0f ? m_timeLimitSeconds : float.PositiveInfinity;
        Debug.Log($"[라운드] 시작 — 목표 {TargetFund}원, 제한시간 {(float.IsPositiveInfinity(RemainingSeconds) ? "무제한" : $"{RemainingSeconds:0}초")}");
        OnRoundStarted?.Invoke();
    }

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
        TickWipeoutRecheck();

        if (m_phase != RoundPhase.InProgress || float.IsPositiveInfinity(RemainingSeconds))
            return;

        RemainingSeconds -= Time.deltaTime;
        if (RemainingSeconds > 0f)
            return;
        RemainingSeconds = 0f;

        if (IsTargetMet)
        {
            Debug.Log($"[라운드] 제한시간 종료 — 목표 달성 상태({CurrentFund}/{TargetFund}원)로 성공 처리");
            EndRound(RoundResult.Success, RoundEndReason.QuotaMet);
            return;
        }

        Debug.Log($"[라운드] 제한시간 초과 — {CurrentFund}/{TargetFund}원, 목표 미달");
        EndRound(RoundResult.Failure, RoundEndReason.TimeOver);
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_phase != RoundPhase.InProgress)
            return;

        if (result.Verdict != ArrestVerdict.WantedCriminal)
            return;

        if (!result.IsFirstDelivery)
            return;

        CriminalArrestCount++;
        Debug.Log($"[라운드] 진범 검거 {CriminalArrestCount}명 — 목표 진행 {CurrentFund}/{TargetFund}원");
    }

    /// <summary>본부 종료 버튼으로 라운드를 성공 종료한다. 목표 달성을 서버에서 다시 검증한다.</summary>
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

    /// <summary>탈출한 진범을 검거 수 집계에서 뺀다. 서버(또는 오프라인) 전용.</summary>
    public void ReportCriminalEscaped()
    {
        if (m_phase != RoundPhase.InProgress)
            return;

        if (CriminalArrestCount <= 0)
            return;

        CriminalArrestCount--;
        Debug.Log($"[라운드] 진범 탈출 — 진범 검거 {CriminalArrestCount}명, 목표 진행 {CurrentFund}/{TargetFund}원");
    }

    private int m_wipeoutRecheckFrame = -1;

    private void HandleClientDisconnected(ulong clientId) => m_wipeoutRecheckFrame = Time.frameCount;

    private void TickWipeoutRecheck()
    {
        if (m_wipeoutRecheckFrame < 0 || Time.frameCount <= m_wipeoutRecheckFrame)
            return;

        m_wipeoutRecheckFrame = -1;
        HandleAnyIncapacitatedChanged();
    }

    private void HandleAnyIncapacitatedChanged()
    {
        if (m_phase != RoundPhase.InProgress)
            return;
        if (!AreAllPlayersOutOfAction())
            return;

        Debug.Log("[라운드] 플레이어 전원 행동불능(다운/기능 정지) — 전멸(게임오버)");
        EndRound(RoundResult.Failure, RoundEndReason.AllPlayersDown);
    }

    private static bool AreAllPlayersOutOfAction()
    {
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

    /// <summary>라운드를 종료한다 — 성공(할당량 달성)·실패(제한시간 초과/전멸) 공통 경로.</summary>
    public void EndRound(RoundResult result, RoundEndReason reason)
    {
        if (m_phase == RoundPhase.Ended)
            return;

        m_phase = RoundPhase.Ended;
        Result = result;
        EndReason = reason;
        m_endedTargetFund = TargetFund;
        SetNpcsFrozen(true);
        Debug.Log($"[라운드] 종료 — 결과: {result} (사유: {reason})");
        OnRoundEnded?.Invoke(result, reason);
    }

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
