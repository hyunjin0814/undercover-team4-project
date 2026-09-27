using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰형 돌발 이벤트의 공통 골격 — NPC 스폰, 경범죄 마커·보상, 소란 타이머, 판정 수신, 잔류 전환, 정리를 맡는다.
/// NPC 행동은 파생 클래스의 ApplyBehavior가 정하며, 매니저와 같은 오브젝트에 둔다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public abstract class SpawnedNpcEventBase : MonoBehaviour, ISuddenEvent
{
    [Header("이벤트 정의")]
    [Tooltip("로그·HUD에 표시할 이름 (예: 동네 깡패 / 공연음란범 / 소매치기)")]
    [SerializeField]
    private string m_displayName = "동네 깡패";

    [Header("스폰 NPC 프리팹")]
    [SerializeField]
    private NpcController m_npcPrefab;

    [Header("스폰 위치 — 현장 플레이어 기준 거리(m)")]
    [Tooltip("무작위로 고른 현장 플레이어에서 이 범위(min~max) 안에 스폰한다")]
    [SerializeField]
    private float m_spawnDistanceMin = 6f;

    [SerializeField]
    private float m_spawnDistanceMax = 12f;

    [Tooltip("스폰 후보 지점에서 이 거리(m) 안에 NavMesh가 없으면 그 지점은 버린다")]
    [SerializeField]
    private float m_navSampleMaxDistance = 4f;

    [Tooltip("유효한 스폰 지점을 찾는 최대 시도 횟수")]
    [SerializeField]
    private int m_maxSpawnAttempts = 8;

    [Tooltip("2명 이상 스폰할 때 앵커 지점 주위로 흩뿌리는 반경(m) — 좁게 잡아야 한 덩어리로 읽힌다 (#721)")]
    [Min(0f)]
    [SerializeField]
    private float m_clusterRadius = 2.5f;

    [Header("경범죄 수익")]
    [Tooltip("본부 인계 후 경범죄 판정 성공 시의 수익 하한 — 스폰 시점에 [하한, 상한]에서 100원 단위로 뽑아 마커에 박는다 (#395)")]
    [Min(0)]
    [SerializeField]
    private int m_pettyCrimeRewardMin = 500;

    [Tooltip("경범죄 수익 상한. 하한보다 작으면 하한이 쓰인다")]
    [Min(0)]
    [SerializeField]
    private int m_pettyCrimeRewardMax = 4000;

    [Header("소란 지속")]
    [Tooltip("제압되지 않고 이 시간(초)이 지나면 진정해 배회 시민으로 잔류한다. 0 이하면 무제한")]
    [SerializeField]
    private float m_maxLifetimeSeconds = 60f;

    private class SpawnedEntry
    {
        public NpcController Npc;

        public Action<NpcState> StateHandler;

        public int Reward;
        public bool Captured;
        public bool ReleaseQueued;
    }

    private readonly List<SpawnedEntry> m_spawned = new List<SpawnedEntry>();

    private ArrestJudge m_arrestJudge;

    protected Transform m_threat;

    private float m_startTime;
    private int m_spawnFrame;
    private bool m_pendingStart;
    private bool m_hasStarted;

    public string DisplayName => m_displayName;

    public bool IsActive => m_spawned.Count > 0;

    protected NpcController PrimaryNpc => m_spawned.Count > 0 ? m_spawned[0].Npc : null;

    public virtual bool AnnounceOnBegin => true;

    public virtual string NoticeKey => null;

    protected virtual int SpawnCount => 1;

    /// <summary>스폰 다음 프레임에 이 NPC가 취할 행동을 설정한다. 서버 전용.</summary>
    protected abstract void ApplyBehavior(NpcController npc);

    protected abstract ERiotBehavior RiotBehavior { get; }

    /// <summary>스폰 직후 개체별 초기 설정을 적용한다(FSM은 건드리지 말 것). 서버 전용.</summary>
    protected virtual void OnSpawned(NpcController npc) { }

    /// <summary>파생 고유의 진행 틱. true를 돌려주면 공통 타이머를 건너뛴다.</summary>
    protected virtual bool OnServerTick() => false;

    /// <summary>제압당한 순간 — 파생이 결말을 얹는다(예: 소매치기가 훔친 물건을 떨군다). 서버 전용.</summary>
    protected virtual void OnCaptured(NpcController npc) { }

    /// <summary>이벤트가 NPC에서 손을 뗄 때 파생의 구독을 정리한다.</summary>
    protected virtual void OnReleasing(NpcController npc) { }

    /// <summary>NPC가 통째로 사라진다 — 라운드 종료 등의 정리 경로. 파생은 딸린 것까지 함께 없앤다.</summary>
    protected virtual void OnDespawning(NpcController npc) { }

    protected virtual void Awake()
    {
        m_arrestJudge = App.Game.ArrestJudge;
        if (m_arrestJudge == null)
            Debug.LogWarning($"{GetType().Name}({m_displayName}): ArrestJudge를 찾지 못해 인계 후 스폰물이 정리되지 않는다", this);
    }

    protected virtual void OnEnable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
    }

    protected virtual void OnDisable()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
    }

    public virtual bool CanTrigger()
    {
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    /// <summary>강제 발동 준비 훅. 기본은 거절한다.</summary>
    public virtual bool ServerPrepareForceTrigger() => false;

    public virtual void ServerBegin()
    {
        if (m_npcPrefab == null)
        {
            Debug.LogWarning($"{GetType().Name}({m_displayName}): NPC 프리팹이 지정되지 않음", this);
            return;
        }

        Transform player = SuddenEventUtil.FindRandomFieldPlayer();
        if (player == null)
            return;

        int areaMask = SuddenEventUtil.SpawnAreaMask(m_npcPrefab);
        if (!SuddenEventUtil.TryFindSpawnPositionNear(
                player.position, m_spawnDistanceMin, m_spawnDistanceMax, m_navSampleMaxDistance, m_maxSpawnAttempts,
                areaMask,
                out Vector3 anchor, hiddenFromPlayers: true))
        {
            Debug.LogWarning($"{GetType().Name}({m_displayName}): NavMesh 위 스폰 지점을 찾지 못해 발생 취소", this);
            return;
        }

        int count = Mathf.Max(1, SpawnCount);
        for (int i = 0; i < count; i++)
            SpawnOne(i == 0 ? anchor : ScatterAround(anchor, areaMask));

        if (m_spawned.Count == 0)
            return;

        m_threat = player;
        m_startTime = Time.time;
        m_spawnFrame = Time.frameCount;
        m_pendingStart = true;
        m_hasStarted = false;
    }

    public void ServerTick()
    {
        if (m_spawned.Count == 0)
            return;

        if (m_pendingStart && Time.frameCount > m_spawnFrame)
        {
            for (int i = 0; i < m_spawned.Count; i++)
            {
                if (!m_spawned[i].ReleaseQueued)
                    ApplyBehavior(m_spawned[i].Npc);
            }

            m_pendingStart = false;
            m_hasStarted = true;
        }

        bool released = false;
        for (int i = m_spawned.Count - 1; i >= 0; i--)
        {
            if (!m_spawned[i].ReleaseQueued)
                continue;

            ReleaseToCity(m_spawned[i]);
            released = true;
        }

        if (released)
            return;

        if (OnServerTick())
            return;

        if (AnyEscorted())
            m_startTime = Time.time;

        if (m_maxLifetimeSeconds > 0f && Time.time - m_startTime > m_maxLifetimeSeconds)
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 소란 지속 시간 종료, 진정");
            for (int i = m_spawned.Count - 1; i >= 0; i--)
            {
                SpawnedEntry entry = m_spawned[i];
                if (entry.Npc != null)
                    entry.Npc.Reaction.StartFlee(null);
                ReleaseToCity(entry);
            }
        }
    }

    public virtual void ServerReset()
    {
        for (int i = m_spawned.Count - 1; i >= 0; i--)
            Despawn(m_spawned[i], playVfx: false);
    }

    /// <summary>스폰물 하나를 도심에 남기지 않고 완전히 제거한다.</summary>
    protected void ServerDespawnSpawned(NpcController npc, bool playVfx = true)
    {
        SpawnedEntry entry = FindEntry(npc);
        if (entry != null)
            Despawn(entry, playVfx);
    }

    private void SpawnOne(Vector3 position)
    {
        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        NpcController npc = Instantiate(m_npcPrefab, position, rotation);

        MisdemeanorOffender offender = npc.gameObject.AddComponent<MisdemeanorOffender>();

        int reward = BountyRoll.Roll(m_pettyCrimeRewardMin, m_pettyCrimeRewardMax);
        offender.Reward = reward;
        offender.SetRiotBehavior(RiotBehavior, m_maxLifetimeSeconds);

        if (SuddenEventUtil.IsNetworkSessionActive)
            npc.GetComponent<NetworkObject>().Spawn();

        OnSpawned(npc);

        SpawnedEntry entry = new SpawnedEntry { Npc = npc, Reward = reward };
        entry.StateHandler = state => HandleStateChanged(entry, state);
        npc.OnStateChanged += entry.StateHandler;
        m_spawned.Add(entry);
    }

    private Vector3 ScatterAround(Vector3 anchor, int areaMask)
    {
        Vector2 offset = Random.insideUnitCircle * m_clusterRadius;
        Vector3 candidate = anchor + new Vector3(offset.x, 0f, offset.y);

        return NavMesh.SamplePosition(candidate, out NavMeshHit hit, m_navSampleMaxDistance, areaMask)
            ? hit.position
            : anchor;
    }

    private void HandleStateChanged(SpawnedEntry entry, NpcState state)
    {
        if (entry.Npc == null)
            return;

        if (state == NpcState.Captured)
        {
            OnCaptured(entry.Npc);

            m_startTime = Time.time;
            if (!entry.Captured)
            {
                entry.Captured = true;
                Debug.Log($"[돌발이벤트] {m_displayName} 제압 — 본부로 연행하면 경범죄 처리(수익 {entry.Reward})");
            }
            return;
        }

        if (state == NpcState.Dead)
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 사망, 이벤트에서 이탈 (시체 인계 시 경범죄 판정)");
            entry.ReleaseQueued = true;
            return;
        }

        if (m_hasStarted && (state == NpcState.Idle || state == NpcState.Walk))
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 제압 실패, 도심에 잔류");
            entry.ReleaseQueued = true;
        }
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        SpawnedEntry entry = FindEntry(result.Npc);
        if (entry == null)
            return;

        Debug.Log($"[돌발이벤트] {m_displayName} — 경범죄 판정, 유치장 인계 (이벤트에서 이탈)");
        ReleaseToCity(entry);
    }

    private void ReleaseToCity(SpawnedEntry entry)
    {
        NpcController npc = entry.Npc;

        if (npc != null)
            OnReleasing(npc);
        Detach(entry);

        if (npc != null)
            MisdemeanorLoiterer.Attach(npc, m_displayName);
    }

    private void Despawn(SpawnedEntry entry, bool playVfx)
    {
        NpcController npc = entry.Npc;
        if (npc == null)
        {
            Detach(entry);
            return;
        }

        OnDespawning(npc);
        Detach(entry);

        foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(npc))
            escorter.ReleaseDrag(npc);

        SuddenEventUtil.DespawnOrDestroy(npc.gameObject, playVfx);
    }

    private void Detach(SpawnedEntry entry)
    {
        if (entry.Npc != null && entry.StateHandler != null)
            entry.Npc.OnStateChanged -= entry.StateHandler;

        entry.StateHandler = null;
        m_spawned.Remove(entry);

        if (m_spawned.Count == 0)
            ClearRun();
    }

    private void ClearRun()
    {
        m_threat = null;
        m_pendingStart = false;
        m_hasStarted = false;
    }

    private SpawnedEntry FindEntry(NpcController npc)
    {
        if (npc == null)
            return null;

        for (int i = 0; i < m_spawned.Count; i++)
            if (m_spawned[i].Npc == npc)
                return m_spawned[i];

        return null;
    }

    private bool AnyEscorted()
    {
        for (int i = 0; i < m_spawned.Count; i++)
            if (m_spawned[i].Npc != null && m_spawned[i].Npc.CurrentState == NpcState.Escorted)
                return true;

        return false;
    }
}
