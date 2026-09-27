using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 범인 탈출 돌발 이벤트 — 침입자가 본부 유치장 자물쇠를 열어 수감자를 탈출시킨다(GDD 6-4).
/// 수감자가 있으면 발동하고, 이동·해제 구간에 침입자를 제압하면 막을 수 있다. 서버 권위.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class JailbreakEvent : MonoBehaviour, ISuddenEvent
{
    [Header("침입자 프리팹 (NpcController)")]
    [SerializeField] private NpcController m_intruderPrefab;

    [Header("유치장 / 자물쇠 (비우면 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;
    [SerializeField] private JailLock m_jailLock;

    [Header("자물쇠 해제")]
    [Tooltip("자물쇠에 도달한 뒤 해제까지 걸리는 시간(초) — 경보를 듣고 달려와 막을 수 있는 구간")]
    [SerializeField] private float m_unlockSeconds = 10f;

    [Header("스폰 위치 보정")]
    [Tooltip("고른 스폰 포인트를 중심으로 이 반경(m) 안에 흩어 배치한다 (NpcSpawner와 같은 방식)")]
    [SerializeField] private float m_spawnRadius = 5f;
    [Tooltip("스폰 후보 지점에서 이 거리(m) 안에 NavMesh가 없으면 그 지점은 버린다")]
    [SerializeField] private float m_navSampleMaxDistance = 4f;
    [Tooltip("유효한 스폰 지점을 찾는 최대 시도 횟수")]
    [SerializeField] private int m_maxSpawnAttempts = 8;

    [Header("경범죄 수익")]
    [Tooltip("침입자를 제압·연행해 인계했을 때의 수익 하한 — 스폰 시점에 [하한, 상한]에서 100원 단위로 뽑아 마커에 박는다 (#395)")]
    [Min(0)]
    [SerializeField] private int m_intruderRewardMin = 500;

    [Tooltip("침입자 수익 상한. 하한보다 작으면 하한이 쓰인다")]
    [Min(0)]
    [SerializeField] private int m_intruderRewardMax = 4000;

    [Header("잔류 전환")]
    [Tooltip("제압되지 않은 채 이 시간(초)이 지나면 침입을 포기하고 배회 시민으로 잔류한다 — 마커가 남아 언제든 잡으면 경범죄 수익 (#310)")]
    [SerializeField] private float m_maxLifetimeSeconds = 90f;

    private NpcSpawner Spawner => App.Game.NpcSpawner;
    private WantedListManager WantedList => App.Game.WantedList;
    private RoundManager Round => App.Game.Round;
    private ArrestJudge Judge => App.Game.ArrestJudge;
    private SuddenEventManager SuddenEvents => App.Game.SuddenEvent;

    private NpcController m_intruder;
    private bool m_pendingStart;
    private bool m_hasStarted;
    private bool m_releaseQueued;
    private bool m_unlockAnnounced;
    private int m_spawnFrame;
    private float m_lifetimeStart;

    private readonly List<NpcController> m_releaseBuffer = new List<NpcController>();

    public string DisplayName => "범인 탈출";

    public bool IsActive => m_intruder != null;

    public bool AnnounceOnBegin => false;

    public string NoticeKey => "Hud.Event.Notice.Jailbreak";

    private void Awake()
    {
        if (m_jailZone == null)
            m_jailZone = App.Game.Jail;
        if (m_jailLock == null)
            m_jailLock = App.Game.JailLock;
    }

    private void Start()
    {
        if (Judge != null)
            Judge.OnArrestJudged += HandleArrestJudged;
        else
            Debug.LogWarning("JailbreakEvent: ArrestJudge를 찾지 못해 검거된 침입자를 놓아주지 못한다", this);
    }

    private void OnDestroy()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    public bool CanTrigger()
    {
        if (m_intruderPrefab == null || m_jailZone == null || m_jailLock == null)
            return false;
        if (Spawner == null || Spawner.SpawnPoints == null || Spawner.SpawnPoints.Count == 0)
            return false;

        if (m_jailZone.Inmates.Count <= 0)
            return false;

        return true;
    }

    public void ServerBegin()
    {
        if (m_intruderPrefab == null || m_jailLock == null)
        {
            Debug.LogWarning("JailbreakEvent: 침입자 프리팹 또는 자물쇠가 없어 발동 취소", this);
            return;
        }

        if (!TryFindSpawnPosition(out Vector3 spawnPosition))
        {
            Debug.LogWarning("JailbreakEvent: NavMesh 위 스폰 지점을 찾지 못해 발동 취소", this);
            return;
        }

        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        m_intruder = Instantiate(m_intruderPrefab, spawnPosition, rotation);

        m_intruder.gameObject.AddComponent<MisdemeanorOffender>().Reward =
            BountyRoll.Roll(m_intruderRewardMin, m_intruderRewardMax);

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_intruder.GetComponent<NetworkObject>().Spawn();

        m_intruder.Intruder.OnIntrudeUnlockStarted += HandleUnlockStarted;
        m_intruder.Intruder.OnIntrudeFinished += HandleIntrudeFinished;
        m_intruder.OnStateChanged += HandleStateChanged;

        m_hasStarted = false;
        m_releaseQueued = false;
        m_unlockAnnounced = false;
        m_spawnFrame = Time.frameCount;
        m_pendingStart = true;
        m_lifetimeStart = Time.time;
    }

    public void ServerTick()
    {
        if (m_intruder == null)
            return;

        if (m_pendingStart && Time.frameCount > m_spawnFrame)
        {
            m_intruder.Intruder.StartIntrude(m_jailLock.ApproachPoint, m_unlockSeconds);
            m_pendingStart = false;
            m_hasStarted = true;
        }

        if (m_releaseQueued)
        {
            ReleaseToCity();
            return;
        }

        if (!m_unlockAnnounced
            && m_intruder.CurrentState == NpcState.Intruding
            && m_jailZone != null
            && m_jailZone.Inmates.Count <= 0)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 유치장이 비어 침입 포기");
            m_intruder.Reaction.StartFlee(null);
            return;
        }

        if (m_intruder.CurrentState == NpcState.Escorted)
            m_lifetimeStart = Time.time;

        if (Time.time - m_lifetimeStart > m_maxLifetimeSeconds)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 침입 포기, 잔류");
            m_intruder.Reaction.StartFlee(null);
            ReleaseToCity();
        }
    }

    public void ServerReset()
    {
        Despawn(playVfx: false);
    }

    private bool TryFindSpawnPosition(out Vector3 result)
    {
        IReadOnlyList<Transform> points = Spawner != null ? Spawner.SpawnPoints : null;
        if (points == null || points.Count == 0)
        {
            result = default;
            return false;
        }

        int start = Random.Range(0, points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[(start + i) % points.Count];
            if (point == null)
                continue;

            if (SuddenEventUtil.TryFindSpawnPositionNear(
                    point.position, 0f, m_spawnRadius, m_navSampleMaxDistance, m_maxSpawnAttempts,
                    SuddenEventUtil.SpawnAreaMask(m_intruderPrefab), out result))
                return true;
        }

        result = default;
        return false;
    }

    private void HandleUnlockStarted(NpcController npc)
    {
        if (npc != m_intruder)
            return;

        m_unlockAnnounced = true;

        Debug.Log($"[돌발이벤트] 범인 탈출 — 자물쇠 해제 시작, {m_unlockSeconds}초 후 개방");

        m_jailLock.ServerAnnounceUnlockAttempt();
    }

    private void HandleIntrudeFinished(NpcController npc, bool reached)
    {
        if (npc != m_intruder)
            return;

        m_intruder.Intruder.OnIntrudeFinished -= HandleIntrudeFinished;
        m_intruder.Intruder.OnIntrudeUnlockStarted -= HandleUnlockStarted;

        if (!reached)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입 경로 실패, 불발 정리");
            Despawn();
            return;
        }

        m_jailLock.ServerUnlock();
        ReleaseAllInmates();

        if (SuddenEvents != null)
            SuddenEvents.Announce(DisplayName, NoticeKey);

        m_lifetimeStart = Time.time;
        m_intruder.Reaction.StartFlee(null);
    }

    /// <summary>사이렌 원격 제지로 진행 중인 침입을 취소한다. 서버(또는 오프라인) 전용.</summary>
    public bool ServerRepelIntruder()
    {
        if (m_intruder == null || m_intruder.CurrentState != NpcState.Intruding)
            return false;

        Debug.Log("[돌발이벤트] 범인 탈출 — 사이렌에 저지당해 침입자 도주");
        m_intruder.Reaction.StartFlee(null);
        return true;
    }

    private void HandleStateChanged(NpcState state)
    {
        if (m_intruder == null)
            return;

        if (state == NpcState.Captured || state == NpcState.Escorted)
        {
            m_lifetimeStart = Time.time;
            return;
        }

        if (state == NpcState.Dead)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 사망, 추적 종료");
            m_releaseQueued = true;
            return;
        }

        if (m_hasStarted && (state == NpcState.Idle || state == NpcState.Walk))
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 도심에 잔류");
            m_releaseQueued = true;
        }
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_intruder == null || result.Npc != m_intruder)
            return;

        Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 경범죄 판정, 유치장 인계 (이벤트 종료)");
        ReleaseToCity();
    }

    private void ReleaseAllInmates()
    {
        m_releaseBuffer.Clear();
        foreach (NpcController inmate in m_jailZone.Inmates)
        {
            if (inmate != null)
                m_releaseBuffer.Add(inmate);
        }

        if (m_releaseBuffer.Count > 0)
            m_jailZone.ServerOpenFrontDoors();

        for (int i = 0; i < m_releaseBuffer.Count; i++)
            ReleaseInmate(m_releaseBuffer[i], i);

        Debug.Log($"[돌발이벤트] 범인 탈출 — 수감자 {m_releaseBuffer.Count}명 방출");
    }

    private void ReleaseInmate(NpcController inmate, int slot)
    {
        m_jailZone.ReleaseInmate(inmate);

        inmate.Custody.ClearDelivered();

        CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
        if (identity != null && identity.IsCriminal)
        {
            if (Round != null)
                Round.ReportCriminalEscaped();
            if (WantedList != null)
                WantedList.ReinstateByNpcId(inmate.NetworkObjectId);
        }

        inmate.Custody.ServerExitJail(m_jailZone.ExitSlot(slot));

        inmate.Reaction.StartFlee(m_intruder != null ? m_intruder.transform : null);

        MisdemeanorLoiterer.BeginRiot(inmate);
    }

    private void StopTracking()
    {
        if (m_intruder == null)
            return;

        m_intruder.Intruder.OnIntrudeUnlockStarted -= HandleUnlockStarted;
        m_intruder.Intruder.OnIntrudeFinished -= HandleIntrudeFinished;
        m_intruder.OnStateChanged -= HandleStateChanged;

        m_intruder = null;
        m_pendingStart = false;
        m_hasStarted = false;
        m_releaseQueued = false;
        m_unlockAnnounced = false;
    }

    private void ReleaseToCity()
    {
        NpcController intruder = m_intruder;
        StopTracking();
        MisdemeanorLoiterer.Attach(intruder, DisplayName);
    }

    private void Despawn(bool playVfx = true)
    {
        if (m_intruder == null)
            return;

        NpcController intruder = m_intruder;
        StopTracking();

        foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(intruder))
            escorter.ReleaseDrag(intruder);

        SuddenEventUtil.DespawnOrDestroy(intruder.gameObject, playVfx);
    }
}
