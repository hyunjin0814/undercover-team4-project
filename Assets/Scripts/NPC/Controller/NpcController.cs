using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// NPC 두뇌 — FSM/NavMesh 구동과 상태 동기화를 담당한다.
/// 이동·상태 판단은 서버 전용이고, 클라는 NetworkTransform·NetworkVariable 결과만 표현한다.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(NpcCustody))]
[RequireComponent(typeof(NpcDeath))]
[RequireComponent(typeof(NpcHealth))]
[RequireComponent(typeof(NpcIntruder))]
[RequireComponent(typeof(NpcKnockback))]
[RequireComponent(typeof(NpcDutyAgent))]
[RequireComponent(typeof(NpcReaction))]
[RequireComponent(typeof(NpcRopeDrag))]
[RequireComponent(typeof(NpcStandUp))]
[RequireComponent(typeof(NpcStun))]
public class NpcController : NetworkBehaviour
{
    [Header("상태별 튜닝 데이터 (ScriptableObject) — #259")]
    [Tooltip("각 FSM 상태가 자기 config를 주입받아 읽는다. 값 조정은 이 에셋들에서 한다.")]
    [SerializeField] private NpcIdleConfig m_idleConfig;
    [SerializeField] private NpcWalkConfig m_walkConfig;
    [SerializeField] private NpcEscortConfig m_escortConfig;
    [SerializeField] private NpcFleeConfig m_fleeConfig;
    [SerializeField] private NpcResistConfig m_resistConfig;
    [SerializeField] private NpcStunConfig m_stunConfig;
    [SerializeField] private NpcCapturedConfig m_capturedConfig;
    [SerializeField] private NpcChaseConfig m_chaseConfig;
    [SerializeField] private NpcCommonConfig m_commonConfig;
    [SerializeField] private NpcRopeDragConfig m_ropeDragConfig;
    [SerializeField] private NpcRepathConfig m_repathConfig;

    private NavMeshAgent m_agent;
    private NpcStateMachine m_stateMachine;
    private NpcRepathScheduler m_repath;

    private NpcCustody m_custody;
    private NpcDeath m_death;
    private NpcHealth m_health;
    private NpcIntruder m_intruder;
    private NpcKnockback m_knockback;
    private NpcDutyAgent m_penalty;
    private NpcReaction m_reaction;
    private NpcRopeDrag m_rope;

    private NpcRagdoll m_ragdoll;
    private NpcStandUp m_standUp;
    private NpcStun m_stun;

    private bool m_frozen;

    private int m_prefabAreaMask;

    private int m_grantedAreas;

    private int m_requestedAreas;
    private float m_areaGrantProbeSeconds;

    private bool m_roadEgressPending;
    private float m_roadEgressProbeSeconds;

    private readonly NetworkVariable<NpcState> m_networkState = new NetworkVariable<NpcState>(NpcState.Idle);

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;

    public NpcRepathScheduler Repath => m_repath;

    internal NpcChaseConfig ChaseConfig => m_chaseConfig;
    internal NpcResistConfig ResistConfig => m_resistConfig;
    internal NpcStunConfig StunConfig => m_stunConfig;
    internal NpcCommonConfig CommonConfig => m_commonConfig;
    internal NpcRopeDragConfig RopeDragConfig => m_ropeDragConfig;

    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    public event Action<NpcState> OnStateChanged;

    public NpcCustody Custody => m_custody;
    public NpcDeath Death => m_death;
    public NpcHealth Health => m_health;
    public NpcIntruder Intruder => m_intruder;
    public NpcKnockback Knockback => m_knockback;
    public NpcDutyAgent Penalty => m_penalty;
    public NpcRagdoll Ragdoll => m_ragdoll;
    public NpcReaction Reaction => m_reaction;
    public NpcRopeDrag Rope => m_rope;
    public NpcStandUp StandUp => m_standUp;
    public NpcStun Stun => m_stun;

    private static readonly System.Collections.Generic.List<NpcController> s_instances = new();

    public static System.Collections.Generic.IReadOnlyList<NpcController> All => s_instances;

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
        m_lastProbePosition = transform.position;

        m_prefabAreaMask = m_agent.areaMask;

        m_custody = GetComponent<NpcCustody>();
        m_death = GetComponent<NpcDeath>();
        m_health = GetComponent<NpcHealth>();
        m_intruder = GetComponent<NpcIntruder>();
        m_knockback = GetComponent<NpcKnockback>();
        m_penalty = GetComponent<NpcDutyAgent>();
        m_reaction = GetComponent<NpcReaction>();
        m_rope = GetComponent<NpcRopeDrag>();
        m_ragdoll = GetComponent<NpcRagdoll>();
        m_standUp = GetComponent<NpcStandUp>();
        m_stun = GetComponent<NpcStun>();

        m_repath = new NpcRepathScheduler(m_repathConfig, transform);

        m_stateMachine = new NpcStateMachine();
        m_stateMachine.AddState(NpcState.Idle, new NpcIdleState(this, m_idleConfig));
        m_stateMachine.AddState(NpcState.Walk, new NpcWalkState(this, m_walkConfig));
        m_stateMachine.AddState(NpcState.Captured, new NpcCapturedState(this, m_capturedConfig));
        m_stateMachine.AddState(NpcState.Escorted, new NpcEscortedState(this, m_escortConfig));
        m_stateMachine.AddState(NpcState.Run, new NpcFleeState(this, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Attack, new NpcResistState(this, m_resistConfig, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Stunned, new NpcStunnedState(this, m_stunConfig));
        m_stateMachine.AddState(NpcState.Jailed, new NpcJailedState(this));
        m_stateMachine.AddState(NpcState.Intruding, new NpcIntrudeState(this));
        m_stateMachine.AddState(NpcState.Detained, new NpcDetainedState(this));
        m_stateMachine.AddState(NpcState.Chasing, new NpcChaseState(this, m_chaseConfig, m_walkConfig, m_fleeConfig));
        m_stateMachine.AddState(NpcState.PenaltyEscorting, new NpcPenaltyEscortState(this, m_escortConfig));
        m_stateMachine.AddState(NpcState.Dead, new NpcDeadState(this));
        m_stateMachine.AddState(NpcState.Sprinting, new NpcSprintState(this, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Smuggling, new NpcSmuggleState(this));

        m_stateMachine.OnBeforeEnter += RevokeGrantedAreasOnCityLife;
        m_stateMachine.OnBeforeEnter += ApplyRoadPolicy;

        m_stateMachine.OnStateChanged += HandleFsmStateChanged;
    }

    public override void OnNetworkSpawn()
    {
        m_networkState.OnValueChanged += HandleNetworkStateChanged;

        if (IsServer)
        {
            InitBehavior();
        }
        else
        {
            m_agent.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_networkState.OnValueChanged -= HandleNetworkStateChanged;
    }

    private void Start()
    {
        if (!IsSpawned)
            InitBehavior();
    }

    /// <summary>배회 파라미터 초기화 + FSM 시동. 서버(또는 오프라인)에서 1회 호출.</summary>
    private void InitBehavior()
    {
        m_agent.speed *= Random.Range(m_commonConfig.SpawnSpeedMultiplierMin, m_commonConfig.SpawnSpeedMultiplierMax);

        m_agent.avoidancePriority = Random.Range(30, 71);

        m_health.InitHealth();

        m_rope.InitDragWeight();

        m_stateMachine.ChangeState(NpcState.Idle);
    }

    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_frozen)
            return;

        TickStuckOffNavMesh();

        if (m_agent.enabled && !m_agent.isOnNavMesh)
            return;

        TickRoadEgress();

        if (m_death.IsDead)
            return;

        TickAreaGrant();

        m_health.Tick();

        if (m_ragdoll == null || !m_ragdoll.IsRagdollActive)
            m_rope.Tick();

        m_standUp.Tick();

        if (m_knockback.IsKnockedBack)
        {
            m_knockback.Tick();
            return;
        }

        if (m_stun.HasStunOverlay)
        {
            m_stun.Tick();
            return;
        }

        m_stateMachine.Tick();
    }

    private void HandleFsmStateChanged(NpcState state)
    {
        if (state != NpcState.Escorted && state != NpcState.Captured)
        {
            m_rope.ClearTethers();
            m_custody.SetSecuredByPlayer(false);
            m_custody.ClearEscortTarget();
        }

        if (!IsSpawned)
        {
            OnStateChanged?.Invoke(state);
            return;
        }

        if (!IsServer)
        {
            Debug.LogWarning($"NpcController: 클라이언트에서 FSM 전이 시도({state}) — 서버 권위라 무시됨", this);
            return;
        }

        m_networkState.Value = state;
    }

    private void HandleNetworkStateChanged(NpcState previous, NpcState current)
    {
        OnStateChanged?.Invoke(current);
    }

    public event Action OnStandUp;

    /// <summary>일어나는 모션을 전 피어에 알린다. 서버(또는 오프라인) 전용.</summary>
    public void RaiseStandUp()
    {
        OnStandUp?.Invoke();
        if (IsSpawned && IsServer)
            PlayStandUpClientRpc();
    }

    [ClientRpc]
    private void PlayStandUpClientRpc()
    {
        if (IsServer)
            return;
        OnStandUp?.Invoke();
    }

    /// <summary>라운드 종료 시 FSM 틱과 NavMesh 이동을 멈추거나 재개한다. 서버(또는 오프라인) 전용.</summary>
    public void SetFrozen(bool frozen)
    {
        if (IsSpawned && !IsServer)
            return;

        m_frozen = frozen;

        if (AgentReady)
            m_agent.isStopped = frozen;
    }

    public bool AgentReady => m_agent != null && m_agent.enabled && m_agent.isOnNavMesh;

    private const float k_warpSnapRadius = 2f;

    /// <summary>에이전트가 준비됐을 때만 isStopped를 바꾼다.</summary>
    internal void SetAgentStopped(bool stopped)
    {
        if (AgentReady)
            m_agent.isStopped = stopped;
    }

    /// <summary>origin 주변 NavMesh 지점을 찾아 에이전트를 붙인다. 붙었으면 true.</summary>
    internal bool TryWarpNear(Vector3 origin)
    {
        if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, k_warpSnapRadius, NavMesh.AllAreas))
            return false;

        return m_agent.Warp(hit.position) && m_agent.isOnNavMesh;
    }

    private static readonly RaycastHit[] s_sweepBuffer = new RaycastHit[16];

    internal bool SweepHitsObstacle(Vector3 direction, float distance, out RaycastHit obstacle)
    {
        obstacle = default;

        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_commonConfig.KnockbackObstacleMask & ~(1 << gameObject.layer);

        int count = Physics.SphereCastNonAlloc(origin, radius, direction, s_sweepBuffer, distance, mask,
                                               QueryTriggerInteraction.Ignore);

        if (count == s_sweepBuffer.Length)
            Debug.LogWarning($"NpcController: 스윕 버퍼 포화({count}) — 히트 누락 가능", this);

        bool found = false;
        for (int i = 0; i < count; i++)
        {
            Collider hit = s_sweepBuffer[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerHealth>() != null)
                continue;
            if (hit.GetComponentInParent<NpcController>() != null)
                continue;

            if (s_sweepBuffer[i].distance <= 0.001f)
                continue;

            if (!found || s_sweepBuffer[i].distance < obstacle.distance)
            {
                obstacle = s_sweepBuffer[i];
                found = true;
            }
        }

        return found;
    }

    internal int BaseAreaMask => m_prefabAreaMask | m_grantedAreas;

    /// <summary>유치장 출입 동안 추가로 허용할 통행 영역을 덮어쓴다. 서버(또는 오프라인) 전용.</summary>
    internal void SetGrantedAreas(int areas)
    {
        m_requestedAreas = areas;
        ApplyGrantedAreas();
    }

    /// <summary>추가 통행 영역을 반영한다 — 얻는 것은 즉시, 잃는 것은 발밑 영역을 벗어난 뒤.</summary>
    private void ApplyGrantedAreas()
    {
        int losing = m_grantedAreas & ~m_requestedAreas;
        int keep = losing != 0 ? losing & NpcNavAreas.AreaMaskAt(transform.position) : 0;
        int next = m_requestedAreas | keep;

        if (next == m_grantedAreas)
            return;

        m_grantedAreas = next;

        ApplyRoadPolicy(m_stateMachine.CurrentState);
    }

    /// <summary>Idle·Walk로 돌아오면 유치장 통행 영역 반납을 요청한다.</summary>
    private void RevokeGrantedAreasOnCityLife(NpcState next)
    {
        if (next is NpcState.Idle or NpcState.Walk)
            SetGrantedAreas(0);
    }

    private void TickAreaGrant()
    {
        if (m_grantedAreas == m_requestedAreas)
            return;

        m_areaGrantProbeSeconds += Time.deltaTime;
        if (m_areaGrantProbeSeconds < k_roadEgressProbeInterval)
            return;
        m_areaGrantProbeSeconds = 0f;

        ApplyGrantedAreas();
    }

    private const float k_roadEgressProbeInterval = 0.25f;

    /// <summary>상태에 맞는 통행 마스크를 건다. 지금 도로 위라면 좁히기를 미룬다.</summary>
    private void ApplyRoadPolicy(NpcState next)
    {
        if (NpcNavAreas.AllowsRoad(next))
        {
            m_roadEgressPending = false;
            SetAreaMask(BaseAreaMask);
            return;
        }

        if (NpcNavAreas.IsOnRoad(transform.position))
        {
            m_roadEgressPending = true;
            m_roadEgressProbeSeconds = 0f;
            SetAreaMask(BaseAreaMask);
            return;
        }

        m_roadEgressPending = false;
        SetAreaMask(NpcNavAreas.ExcludeRoad(BaseAreaMask));
    }

    /// <summary>도로를 벗어나면 미뤄 둔 통행 마스크 좁히기를 적용한다. 서버(또는 오프라인) 전용.</summary>
    private void TickRoadEgress()
    {
        if (!m_roadEgressPending)
            return;

        m_roadEgressProbeSeconds += Time.deltaTime;
        if (m_roadEgressProbeSeconds < k_roadEgressProbeInterval)
            return;
        m_roadEgressProbeSeconds = 0f;

        if (NpcNavAreas.AllowsRoad(m_stateMachine.CurrentState))
        {
            m_roadEgressPending = false;
            return;
        }

        if (!NpcNavAreas.IsOnRoad(transform.position))
        {
            m_roadEgressPending = false;
            SetAreaMask(NpcNavAreas.ExcludeRoad(BaseAreaMask));
            return;
        }

        DriveOffRoad();
    }

    private const float k_roadEgressDistance = 10f;

    private const float k_roadEgressClearance = 2.5f;

    private const int k_roadEgressDirections = 8;

    private const float k_roadEgressSnapRadius = 2f;

    /// <summary>도로 위에 멈춘 NPC를 Walk 상태로 전환해 가장 가까운 도로 밖으로 걸어 나가게 한다.</summary>
    private void DriveOffRoad()
    {
        if (!AgentReady)
            return;

        if (!m_agent.isStopped && m_agent.hasPath && !NpcNavAreas.IsOnRoad(m_agent.destination))
            return;

        if (!TryFindRoadExit(out Vector3 exit))
            return;

        if (m_stateMachine.CurrentState == NpcState.Idle)
            m_stateMachine.ChangeState(NpcState.Walk);

        m_agent.isStopped = false;
        m_agent.SetDestination(exit);
    }

    /// <summary>주변을 둘러 도로에서 충분히 떨어진 지점을 찾는다.</summary>
    private bool TryFindRoadExit(out Vector3 exit)
    {
        exit = default;

        int offRoadMask = NpcNavAreas.ExcludeRoad(BaseAreaMask);
        Vector3 origin = transform.position;

        float bestClearSqr = float.MaxValue;
        bool foundClear = false;

        Vector3 fallback = default;
        float bestAnySqr = float.MaxValue;
        bool foundAny = false;

        for (int i = 0; i < k_roadEgressDirections; i++)
        {
            float angle = i * (Mathf.PI * 2f / k_roadEgressDirections);
            Vector3 candidate = origin
                + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * k_roadEgressDistance;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, k_roadEgressSnapRadius, offRoadMask))
                continue;

            if (NpcNavAreas.IsOnRoad(hit.position))
                continue;

            float sqr = (hit.position - origin).sqrMagnitude;

            if (!NavMesh.SamplePosition(hit.position, out NavMeshHit _, k_roadEgressClearance,
                                        NpcNavAreas.RoadMask))
            {
                if (sqr < bestClearSqr)
                {
                    bestClearSqr = sqr;
                    exit = hit.position;
                    foundClear = true;
                }
                continue;
            }

            if (sqr < bestAnySqr)
            {
                bestAnySqr = sqr;
                fallback = hit.position;
                foundAny = true;
            }
        }

        if (foundClear)
            return true;

        exit = fallback;
        return foundAny;
    }

    /// <summary>통행 마스크를 바꾸고, 바뀌었으면 현재 경로를 다시 계산시킨다.</summary>
    private void SetAreaMask(int mask)
    {
        if (m_agent.areaMask == mask)
            return;

        m_agent.areaMask = mask;

        if (AgentReady && m_agent.hasPath)
            m_agent.SetDestination(m_agent.destination);
    }

    private const float k_stuckFatalSeconds = 8f;

    private const float k_stuckProbeInterval = 0.5f;

    private const float k_stuckProbeRadius = 2f;

    private const float k_stuckHeightAboveMesh = 1.5f;

    private const float k_insideMapRadius = 15f;

    private const float k_stuckStillEpsilon = 0.15f;

    private float m_offNavMeshSeconds;
    private float m_stuckProbeSeconds;
    private Vector3 m_lastProbePosition;

    /// <summary>NavMesh 밖(또는 구조물 위)에 8초간 멈춰 있는 몸을 행방불명·시체로 정리한다. 서버(또는 오프라인) 전용.</summary>
    private void TickStuckOffNavMesh()
    {
        m_stuckProbeSeconds += Time.deltaTime;
        if (m_stuckProbeSeconds < k_stuckProbeInterval)
            return;

        m_stuckProbeSeconds = 0f;

        EStuckKind kind = ProbeStuck();
        if (kind == EStuckKind.None)
        {
            m_offNavMeshSeconds = 0f;
            return;
        }

        m_offNavMeshSeconds += k_stuckProbeInterval;
        if (m_offNavMeshSeconds < k_stuckFatalSeconds)
            return;

        m_offNavMeshSeconds = 0f;

        bool offMap = !NavMesh.SamplePosition(
            transform.position,
            out NavMeshHit _,
            k_insideMapRadius,
            NavMesh.AllAreas
        );

        Debug.LogWarning(
            $"NpcController: {k_stuckFatalSeconds}초간 걸어 나올 수 없던 NPC를 정리했다 "
                + $"({(offMap ? "맵 밖 — 행방불명" : "맵 안 — 시체로 남긴다")}, {kind}): "
                + $"{name} @{transform.position.ToString("F1")}",
            this
        );

        m_health.ServerKillStuck();

        if (!offMap)
            return;

        WantedListManager wanted = App.Game.WantedList;
        if (wanted != null)
            wanted.MarkMissing(NetworkObjectId);

        SuddenEventUtil.DespawnOrDestroy(gameObject, playVfx: false);
    }

    private enum EStuckKind
    {
        None,
        OffNavMesh,
        AboveMesh,
    }

    private EStuckKind ProbeStuck()
    {
        Vector3 previous = m_lastProbePosition;
        m_lastProbePosition = transform.position;

        if (m_death.IsDead || m_rope.IsRoped || m_knockback.IsKnockedBack)
            return EStuckKind.None;

        bool ragdolled = m_ragdoll != null && m_ragdoll.IsRagdollActive;
        if (!m_agent.enabled && !ragdolled)
            return EStuckKind.None;

        if ((transform.position - previous).sqrMagnitude > k_stuckStillEpsilon * k_stuckStillEpsilon)
            return EStuckKind.None;

        if (m_agent.enabled && !m_agent.isOnNavMesh)
            return EStuckKind.OffNavMesh;

        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, k_stuckProbeRadius, NavMesh.AllAreas))
            return EStuckKind.OffNavMesh;

        return transform.position.y - hit.position.y > k_stuckHeightAboveMesh
            ? EStuckKind.AboveMesh
            : EStuckKind.None;
    }
}
