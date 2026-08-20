using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// NPC 두뇌 — FSM/NavMesh 구동과 상태 동기화를 담당한다.
/// 이동·상태 판단은 서버 전용(서버 권위)이고, 클라이언트는
/// NetworkTransform(위치)과 NetworkVariable(상태)로 동기화된 결과만 표현한다. (이슈 #56)
/// 네트워크를 켜지 않은 로컬 Play 테스트에서는 기존처럼 단독으로 동작한다.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
// 도메인 부품 10개 — 누락 시 그 도메인 경로가 NRE로 죽는다 (#503)
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

    // 도메인 부품 — 같은 GameObject에 붙는다. [RequireComponent]로 누락을 막는다. (#503)
    private NpcCustody m_custody;
    private NpcDeath m_death;
    private NpcHealth m_health;
    private NpcIntruder m_intruder;
    private NpcKnockback m_knockback;
    private NpcDutyAgent m_penalty;
    private NpcReaction m_reaction;
    private NpcRopeDrag m_rope;

    // 래그돌 — 밧줄 틱을 돌릴지 가르는 데 쓴다. 리그 없는 프리팩에서는 null이다 (#572).
    private NpcRagdoll m_ragdoll;
    private NpcStandUp m_standUp;
    private NpcStun m_stun;

    // 라운드 종료 정지(freeze) 플래그 — 서버(또는 오프라인)에서만 의미. 켜지면 FSM/이동을 멈춘다.
    private bool m_frozen;

    // 프리팹이 정한 통행 마스크 — Awake에서 1회 확정하고 이후 <b>절대 바뀌지 않는다</b> (#634 후속).
    private int m_prefabAreaMask;

    // 지금 추가로 열어 준 영역(Jail·HQ) — 유치장에 드나드는 동안만 얹힌다 (#744, SetGrantedAreas)
    private int m_grantedAreas;

    // 호출부가 요구한 영역 — m_grantedAreas와 다르면 아직 반영하지 못한 것이다 (TickAreaGrant)
    private int m_requestedAreas;
    private float m_areaGrantProbeSeconds;

    // 도로 위라 아직 마스크를 좁히지 못했다 — 벗어나는 즉시 좁힌다 (TickRoadEgress)
    private bool m_roadEgressPending;
    private float m_roadEgressProbeSeconds;

    // 서버 권위 FSM 상태 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<NpcState> m_networkState = new NetworkVariable<NpcState>(NpcState.Idle);

    public NavMeshAgent Agent => m_agent;
    public NpcStateMachine StateMachine => m_stateMachine;

    /// <summary>재탐색·훑기 주기 게이트 — 상태 클래스가 "지금 다시 계산할 때인가"를 묻는다. (#573)</summary>
    public NpcRepathScheduler Repath => m_repath;

    // 튜닝 SO는 코어가 계속 들고 부품이 여기서 읽는다 (계획서 § 3-3).
    // 부품은 같은 어셈블리라 internal로 족하다. 뒤 주석은 읽는 부품이다. (#503)
    internal NpcChaseConfig ChaseConfig => m_chaseConfig; // NpcDutyAgent — 격퇴 도주 시간
    internal NpcResistConfig ResistConfig => m_resistConfig; // NpcReaction — 위협 탐색 반경
    internal NpcStunConfig StunConfig => m_stunConfig; // NpcStun — 지속 시간·기상 클립 / NpcHealth — 쓰러짐 기절 시간
    internal NpcCommonConfig CommonConfig => m_commonConfig; // NpcHealth·NpcKnockback·NpcRopeDrag
    internal NpcRopeDragConfig RopeDragConfig => m_ropeDragConfig; // NpcRopeDrag — 길이·장력

    /// <summary>
    /// 현재 NPC 상태. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 안전하게 읽을 수 있다.
    /// (StateMachine.CurrentState는 서버에서만 갱신되므로 외부 코드는 반드시 이 프로퍼티를 읽을 것)
    /// </summary>
    public NpcState CurrentState => IsSpawned ? m_networkState.Value : m_stateMachine.CurrentState;

    /// <summary>상태 변경 이벤트 — 서버·클라이언트 모든 피어에서 발생한다. 애니메이션 등 표현 계층이 구독. (#56)</summary>
    public event Action<NpcState> OnStateChanged;

    // 도메인 부품 접근자 — 호출부는 npc.Rope.IsRoped처럼 부품을 거친다 (계획서 § 3-1). (#503)
    /// <summary>신병 — 연행·인계 표식·수감·감옥 퇴장·반출 표식 (#59/#228/#537)</summary>
    public NpcCustody Custody => m_custody;
    /// <summary>사망 — 체력 0에서 되돌아오지 않는 끝으로 넘긴다 (#571)</summary>
    public NpcDeath Death => m_death;
    /// <summary>체력 — HP·피해 적용·회복과 <see cref="IDamageable"/> 구현 (#366)</summary>
    public NpcHealth Health => m_health;
    /// <summary>침입 — 목표·해제 시간·진행 이벤트 (#231)</summary>
    public NpcIntruder Intruder => m_intruder;
    /// <summary>넉백 — 외력 비행과 착지 후 복귀 상태 (#232)</summary>
    public NpcKnockback Knockback => m_knockback;
    /// <summary>특수 임무 — 오검거·납치·소매치기의 수용·추격·수렴·호송 (#277~#279/#371/#303)</summary>
    public NpcDutyAgent Penalty => m_penalty;
    /// <summary>검거 반응 — 위협 대상·도주·저항·스윙 (#76/#205/#213/#220)</summary>
    public NpcReaction Reaction => m_reaction;
    /// <summary>밧줄 — 묶임·끌기·무게 (#269/#369/#398)</summary>
    public NpcRopeDrag Rope => m_rope;
    /// <summary>기상 예약 — 줄이 풀리며 일어나는 구간과 재포획 창 (#513)</summary>
    public NpcStandUp StandUp => m_standUp;
    /// <summary>기절 — 스턴 오버레이·진입·해제 (#292)</summary>
    public NpcStun Stun => m_stun;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();

        // 프리팹이 정한 통행 마스크를 <b>좁히기 전에</b> 잡아 둔다 (#634 후속).
        // 되돌릴 때 NavMesh.AllAreas로 복구하면 프리팹이 일부러 뺀 영역(Jail·HQ)까지 되살아나고,
        // 좁아진 뒤의 m_agent.areaMask를 기준으로 삼으면 한 번 좁힌 뒤 영영 못 되돌린다.
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

        // 상태보다 먼저 만든다 — 상태 클래스가 생성자에서 게이트를 잡을 수 있게. 위상은 여기서 한 번만 흔뿌려진다 (#573)
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
        m_stateMachine.AddState(NpcState.Releasing, new NpcReleasingState(this, m_fleeConfig));
        m_stateMachine.AddState(NpcState.Dead, new NpcDeadState(this));

        // 통행 정책은 새 상태의 Enter()가 목적지를 잡기 <b>전에</b> 걸려야 한다 — 그래서
        // OnStateChanged가 아니라 OnBeforeEnter다 (#634 후속)
        //
        // <b>순서가 계약이다.</b> 유치장 통행 반납이 도로 정책보다 먼저다 — 반납이 뒤로 가면
        // ApplyRoadPolicy가 아직 넓은 기준값으로 마스크를 걸고, Enter()가 그 마스크로 본부 실내에
        // 배회 지점을 잡는다 (#744).
        m_stateMachine.OnBeforeEnter += RevokeGrantedAreasOnCityLife;
        m_stateMachine.OnBeforeEnter += ApplyRoadPolicy;

        // FSM 전이(서버/오프라인에서만 발생)를 동기화 변수 또는 로컬 이벤트로 흘려보낸다
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
            // 클라이언트의 이동은 NetworkTransform이 담당 — NavMeshAgent가 켜져 있으면
            // 동기화로 옮겨진 위치를 NavMesh 위로 되돌리려 해 서로 싸운다
            m_agent.enabled = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_networkState.OnValueChanged -= HandleNetworkStateChanged;
    }

    private void Start()
    {
        // 오프라인 폴백 — 네트워크 세션 없이 Play한 로컬 테스트에서는 기존처럼 단독 구동한다.
        // (네트워크 스폰된 경우 OnNetworkSpawn이 Start보다 먼저 불리므로 여기는 건너뛴다)
        if (!IsSpawned)
            InitBehavior();
    }

    /// <summary>배회 파라미터 초기화 + FSM 시동. 서버(또는 오프라인)에서 1회 호출.</summary>
    private void InitBehavior()
    {
        // 개체마다 걷는 속도를 다르게 해 군중이 같은 리듬으로 움직이는 것을 깨준다
        m_agent.speed *= Random.Range(m_commonConfig.SpawnSpeedMultiplierMin, m_commonConfig.SpawnSpeedMultiplierMax);

        // 회피 우선순위도 개체마다 다르게 — 전원이 같은 값이면 정면으로 마주친 둘이
        // 대칭적으로 서로 양보하다가 교착에 빠진다 (값이 낮은 쪽이 우선권을 가진다)
        m_agent.avoidancePriority = Random.Range(30, 71);

        // 체력은 FSM 시동 전에 채운다 — 첫 틱부터 CurrentHp가 유효해야 한다 (#366)
        m_health.InitHealth();

        // 무게 추첨 — 라운드 내내 유지된다(재검거·탈옥 후에도 같은 값). (#398)
        m_rope.InitDragWeight();

        m_stateMachine.ChangeState(NpcState.Idle);
    }

    private void Update()
    {
        // FSM/NavMesh는 서버 전용 — 클라이언트는 동기화된 위치·상태만 표현한다 (#56)
        if (IsSpawned && !IsServer)
            return;

        // 라운드 종료 freeze — 서버에서 멈추면 NetworkTransform이 정지 위치를 복제해 전 피어에서 멈춘다
        if (m_frozen)
            return;

        // NavMesh 밖에서 굳은 몸의 회수 — 아래 모든 게이트보다 **먼저** 돈다 (#557).
        // 뒤로 내리면 스턴 게이트에 가려 기절한 채 굳은 NPC(=신고된 증상 그대로)에 영영 닿지 못한다.
        TickNavMeshRecovery();

        // 도로를 벗어나면 통행 마스크를 좁힌다 — 회수와 같은 이유로 게이트보다 먼저 돈다 (#634 후속).
        // 도로 위에서 기절·넉백을 맞으면 그 구간 내내 대기 상태로 남는데, 그동안 움직이지 않으므로
        // 판정은 계속 "도로 위"고 좁혀지지 않는다 — 깨어나 걸어 나가면 그때 좁는다.
        TickRoadEgress();

        // 사망 — <b>모든 게이트보다 먼저 끝낸다</b> (#571). 죽은 몸은 아무 틱도 돌지 않는다.
        //
        // 다른 게이트들과 달리 여기서 대신 돌릴 Tick이 없다: 시체의 표현은 래그돌(NpcRagdoll)이
        // 자기 Update에서 로컬로 굴리고, 그건 클라에서도 돌아야 해서(이 Update는 서버 전용이다)
        // 애초에 여기 있을 수 없다.
        //
        // ⚠ <b>밧줄보다 앞인 것이 이제 방어선이 아니라 사양이다</b> (#571 시체 끌기). 시체에도 줄이
        // 걸리는데(밧줄 좌클릭), 그 줄은 <b>관절</b>(RagdollRope)이라 물리가 몸을 끌고 루트는
        // NpcRagdoll.TickRootFollow가 따라붙인다. 아래 m_rope.Tick()은 <c>transform.position</c>을
        // 직접 대입하는 반대편 방식이라(#369), 시체에 돌면 둘이 같은 프레임에 위치를 다퉈 시체가
        // 떨거나 몸을 두고 루트만 날아간다. <b>갈리는 기준은 "대상이 래그돌이냐"다</b>(docs/ragdoll.md §8).
        if (m_death.IsDead)
            return;

        // 미뤄 둔 Jail·HQ 통행 회수 (#744) — <b>사망 게이트 뒤</b>다. 시체는 돌려줄 통행이 없고,
        // 앞에 두면 본부에서 죽은 방출 대상이 영영 끝나지 않는 회수를 매 주기 재시도한다.
        // 기절·넉백은 이 게이트를 지나므로, 멈춰 있는 동안에도 발밑은 계속 확인된다.
        TickAreaGrant();

        // 방치 회복 — 사망 게이트 뒤, 나머지 게이트보다는 앞 (#707)
        m_health.Tick();

        // 밧줄 장력 — 게이트보다 **먼저** (#390). 묶인 채 기절한 대상은 스턴 오버레이를 단 채 끌려가야 하므로,
        // 뒤로 내리면 테이저→밧줄 콤보로 잡은 대상이 그 자리에 멈춘다. (넉백과는 배타적 — StopEscort가 끌기를 정리한다)
        //
        // ⚠ <b>래그돌인 대상에는 돌리지 않는다</b> (#572 3단계). 위 사망 게이트 주석이 적어 둔 기준
        // ("갈리는 기준은 대상이 래그돌이냐다")을 그대로 적용한 것이다 — 예전에는 래그돌 = 시체라
        // 사망 게이트 하나로 같은 효과가 났지만, 기절에도 래그돌이 붙으면서 둘이 갈렸다.
        // 래그돌인 몸은 <b>관절 밧줄</b>(RagdollRope)이 물리로 끌고 루트는 NpcRagdoll.TickRootFollow가
        // 따라붙인다. 여기서 <c>transform.position</c>을 함께 대입하면 같은 프레임에 위치를 다퉈
        // 몸이 떨거나 몸을 두고 루트만 날아간다.
        //
        // <b>return이 아니라 건너뛰기다</b> — 아래 m_stun.Tick()이 기절 타이머를 굴리므로 여기서
        // 끊으면 끌려가는 동안 기절이 영영 안 풀린다.
        if (m_ragdoll == null || !m_ragdoll.IsRagdollActive)
            m_rope.Tick();

        // 줄이 풀리며 일어나는 구간 — 밧줄 장력과 같은 이유로 아래 게이트보다 **먼저** 돈다 (#513).
        // 뒤로 내리면 일어나는 도중 기절·넉백을 맞은 대상의 예약이 영원히 남는다.
        m_standUp.Tick();

        // 넉백 비행 중에는 FSM을 돌리지 않는다 — NavMeshAgent를 꺼 둔 채라 상태 클래스가
        // SetDestination/isStopped를 부르면 "agent not on NavMesh" 에러가 쏟아진다 (#232)
        if (m_knockback.IsKnockedBack)
        {
            m_knockback.Tick();
            return;
        }

        // 스턴 오버레이 중에는 FSM을 돌리지 않는다 — 상태는 그대로 둔 채 제자리에 얼린다.
        // 넉백 게이트 뒤에 두는 게 중요하다: 둘이 겹치면 넉백이 이긴다 (#292)
        if (m_stun.HasStunOverlay)
        {
            m_stun.Tick();
            return;
        }

        m_stateMachine.Tick();
    }

    // 서버(또는 오프라인)의 FSM 전이를 밖으로 전파한다
    private void HandleFsmStateChanged(NpcState state)
    {
        // 커스터디를 벗어나면 신병에 매달린 표식부터 내린다 — 전이와 같은 프레임에 맞아야 한다.
        // 묶임(#513)은 표현(누운 자세)이, 반출(#517)은 E 분기가 이 값을 본다.
        if (state != NpcState.Escorted && state != NpcState.Captured)
        {
            m_rope.ClearTethers();
            m_custody.SetJailExtracted(false);
            m_custody.SetSecuredByPlayer(false); // 도주·배회로 돌아갔다 — 더는 누구의 신병도 아니다 (#637)
            m_custody.ClearEscortTarget(); // 사망·넉백처럼 ReleaseDrag를 안 거치는 이탈도 장부를 남기지 않는다 (#643)
        }

        // 반출 목적지(#548)도 같은 자리에서 내린다 — 단 <b>도주·저항·기절로는 지우지 않는다</b>
        // (2026-08-12 확정). 맞아서 돌변한 것은 그 순간의 반응일 뿐이고, 쓰러뜨려 재우면 깨어나 다시
        // 인도 지점으로 뛴다(NpcStun.ExitStun). 무산은 신병을 잡았을 때뿐 — 밧줄·재수감·사망 (GDD 6-1).
        // NpcReleasingState.Exit이 아니라 여기인 이유: Exit은 "어디로 나가는지"를 모른다.
        if (
            state != NpcState.Releasing
            && state != NpcState.Stunned
            && state != NpcState.Run
            && state != NpcState.Attack
        )
            m_custody.ClearRelease();

        if (!IsSpawned)
        {
            OnStateChanged?.Invoke(state); // 오프라인 — 동기화 없이 바로 로컬 이벤트
            return;
        }

        // 클라이언트에서 실수로 FSM을 전이시켜도 서버 권위 변수 쓰기 예외로 터지지 않게 막는다
        if (!IsServer)
        {
            Debug.LogWarning($"NpcController: 클라이언트에서 FSM 전이 시도({state}) — 서버 권위라 무시됨", this);
            return;
        }

        m_networkState.Value = state; // OnValueChanged를 거쳐 모든 피어에서 OnStateChanged가 발생한다
    }

    private void HandleNetworkStateChanged(NpcState previous, NpcState current)
    {
        OnStateChanged?.Invoke(current);
    }

    /// <summary>기절에서 일어나기 시작할 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 일어나는 구간은 FSM 상태가 여전히 Stunned라(그 동안 움직이지 않는다) 상태 동기화만으로는
    /// 클라이언트가 알 수 없다 — 스윙(<see cref="NpcReaction.OnAttackSwing"/>)과 같은 순간 이벤트로 전달한다. (#269)</summary>
    public event Action OnStandUp;

    /// <summary>일어나는 모션을 전 피어에 알린다 — 서버(또는 오프라인)에서만 호출한다.
    /// 기절 해제(#269)와 밧줄 풀림(#513) 두 경로가 쓴다.</summary>
    public void RaiseStandUp()
    {
        OnStandUp?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayStandUpClientRpc();
    }

    [ClientRpc]
    private void PlayStandUpClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnStandUp?.Invoke();
    }

    /// <summary>라운드 종료 정지 — 서버(또는 오프라인)에서 호출. FSM 틱과 NavMesh 이동을 멈춘다.
    /// 서버에서 멈추면 NetworkTransform이 정지 위치를 복제해 모든 클라이언트에서도 멈춘 것으로 보인다.</summary>
    public void SetFrozen(bool frozen)
    {
        // FSM/이동은 서버 권위 — 클라이언트 호출은 다른 제어 메서드와 동일하게 무시한다
        if (IsSpawned && !IsServer)
            return;

        m_frozen = frozen;

        // 에이전트를 멈춘다 — 비활성/NavMesh 밖이면 isStopped 접근이 예외를 던지므로 가드
        if (AgentReady)
            m_agent.isStopped = frozen;
    }

    /// <summary>
    /// 지금 에이전트를 <b>만져도 되는가</b> — <c>isStopped</c>·<c>SetDestination</c>·<c>ResetPath</c>는
    /// 비활성이거나 NavMesh 밖이면 Unity가 예외를 던진다. (#557)
    ///
    /// ⚠ <b>"왜 못 쓰는가"가 아니라 "쓸 수 있는가"를 묻는 값이다.</b> 에이전트를 꺼 두는 구간이
    /// 넷으로 늘었고(넉백 비행·밧줄 끌기·사망·<b>기절 래그돌</b>, #572) 앞으로도 늘 수 있어서,
    /// 원인을 열거해 추론하면 새 구간이 생길 때마다 조용히 틀린다 —
    /// <c>NpcEscortedState.Enter</c>가 <c>IsRoped</c>로 추론하다 정확히 그렇게 깨졌다.
    /// </summary>
    public bool AgentReady => m_agent != null && m_agent.enabled && m_agent.isOnNavMesh;

    // 워프 기준점 주변에서 NavMesh를 찾을 때의 기본 탐색 반경(m).
    private const float k_warpSnapRadius = 2f;

    /// <summary>
    /// 기준점 주변에서 NavMesh 위 지점을 찾아 에이전트를 붙인다 — 붙었으면 true. (#503)
    ///
    /// 밧줄 놓기(#369)와 감옥 방출(#537)이 함께 쓰는 공용 유틸이라 코어에 둔다 (계획서 § 3-6) —
    /// 부품에 딸려 보내면 "Custody가 Rope를 참조한다"는 가짜 의존이 생긴다.
    /// 실패하면 <b>호출부가</b> 대응한다 — 대안 지점이냐 제자리냐는 도메인마다 다르다.
    /// </summary>
    /// <param name="snapRadius">탐색 반경(m) — 넓히는 건 최후 수단인 회수(<see cref="TickNavMeshRecovery"/>)뿐이다.</param>
    internal bool TryWarpNear(Vector3 origin, float snapRadius = k_warpSnapRadius)
    {
        if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, snapRadius, NavMesh.AllAreas))
            return false;

        return m_agent.Warp(hit.position) && m_agent.isOnNavMesh;
    }

    // 벽 스윕 히트 버퍼 — 스윕은 서버(또는 오프라인) 전용이라 공유해도 안전하다 (프레임마다의 할당 방지)
    private static readonly RaycastHit[] s_sweepBuffer = new RaycastHit[16];

    // 이번 프레임 수평 이동 구간에 벽이 있는지 — 몸통 굵기로 훑는다.
    // 넉백 비행(#232)과 밧줄 끌기(#369)가 함께 쓰는 공용 유틸이라 코어에 둔다 (계획서 § 3-6) —
    // 판정만 공유하고 대응은 호출부가 정한다: 넉백은 그 자리에 떨어지고, 끌기는 벽을 따라 미끄러진다.
    // NavMesh를 충돌 프록시로 쓰면 안 된다 — 실측(Test Scene)에서 벽이 11.8m 밖인 방향이 NavMesh
    // 기준 2.0m에서 "막힘"으로 나왔고, 그걸 벽으로 치면 넉백이 제자리 점프가 된다 (#232).
    // 캐릭터(플레이어·다른 NPC)는 벽으로 치지 않는다 — 플레이어 몸통이 환경과 같은 Default 레이어라
    // 마스크로는 못 거르는데, 군중을 벽으로 오판하면 폭발 넉백이 죄다 제자리에 툭 떨어진다 (#339/#313).
    internal bool SweepHitsObstacle(Vector3 direction, float distance, out RaycastHit obstacle)
    {
        obstacle = default;

        float radius = m_agent.radius;
        Vector3 origin = transform.position + Vector3.up * Mathf.Max(radius, m_agent.height * 0.5f);
        int mask = m_commonConfig.KnockbackObstacleMask & ~(1 << gameObject.layer); // 자기 콜라이더에 걸리지 않게

        int count = Physics.SphereCastNonAlloc(origin, radius, direction, s_sweepBuffer, distance, mask,
                                               QueryTriggerInteraction.Ignore);

        // 버퍼 포화 = 반환되지 못한 히트(그중 진짜 벽 포함 가능)가 있을 수 있다 — 나오면 확대 신호 (#313 리뷰와 동일)
        if (count == s_sweepBuffer.Length)
            Debug.LogWarning($"NpcController: 스윕 버퍼 포화({count}) — 히트 누락 가능", this);

        bool found = false;
        for (int i = 0; i < count; i++)
        {
            Collider hit = s_sweepBuffer[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerHealth>() != null)
                continue; // 플레이어 — 벽이 아니다, 뚫고 날아간다
            if (hit.GetComponentInParent<NpcController>() != null)
                continue; // 다른 NPC — 군중 속 폭발에서 서로를 벽으로 보지 않게

            // 시작 지점에서 이미 겹친 히트(거리 0)는 버린다 — 법선이 진행 방향 반대로 잡혀 어느 쪽으로
            // 움직여도 계속 막히므로, 한 번 끼면 영영 빠져나오지 못한다 (밧줄 끌기에서 실제로 낀 사례, #369).
            // 이미 안에 있는 이상 막는 것보다 빠져나갈 기회를 주는 편이 항상 낫다.
            if (s_sweepBuffer[i].distance <= 0.001f)
                continue;

            // 캐릭터가 아닌 무언가 = 벽/환경. 여럿이면 가장 가까운 것을 남긴다.
            if (!found || s_sweepBuffer[i].distance < obstacle.distance)
            {
                obstacle = s_sweepBuffer[i];
                found = true;
            }
        }

        return found;
    }

    // ---- 도로 통행 정책 (#634 후속) ----

    /// <summary>
    /// 이 몸에 지금 허용된 통행 마스크 — <b>도로 정책이 적용되기 전</b>의 값이다.
    /// 프리팹이 정한 마스크에 <see cref="SetGrantedAreas"/>로 열어 준 영역을 얹은 것.
    ///
    /// "이 몸을 NavMesh 어디에 놓을 수 있는가"를 묻는 쪽(넉백 착지·래그돌 기상)이 쓴다.
    /// 그건 "지금 걸어도 되는 곳인가"와 다른 질문이라 <see cref="NavMeshAgent.areaMask"/>를
    /// 쓰면 안 된다 — 배회 중이라 마스크가 좁아진 몸이 도로 위에 떨어지면 착지점을 못 찾는다.
    /// (Jail·HQ 제외는 부여받지 않은 몸에는 그대로 살아 있어 #415/#722의 이유가 지켜진다)
    /// </summary>
    internal int BaseAreaMask => m_prefabAreaMask | m_grantedAreas;

    /// <summary>
    /// 유치장에 드나드는 동안 <b>추가로</b> 열어 줄 통행 영역 — 서버(또는 오프라인) 전용. (#744)
    ///
    /// 시민 프리팹의 마스크는 Jail·HQ를 빼고 있어(배회 시민이 셀·본부에 걸어 들어오지 못하게) 수감자와
    /// 방출 대상만 그때그때 열어 줘야 한다. 부르는 곳은 셋이고 <b>덮어쓰기</b>다(누적이 아니다):
    /// 수감 시 <c>JailMask</c>, 셀을 나설 때 <c>HqMask</c>, 도시에 정착하면 0.
    ///
    /// <b>프리팹 마스크가 아니라 이 값을 갈아 끼우는 이유</b>는 <see cref="ApplyRoadPolicy"/>가 상태
    /// 전이마다 <see cref="BaseAreaMask"/>로 되돌리기 때문이다 — 에이전트의 <c>areaMask</c>를 직접
    /// 건드리면 다음 전이에서 지워진다.
    /// </summary>
    internal void SetGrantedAreas(int areas)
    {
        m_requestedAreas = areas;
        ApplyGrantedAreas();
    }

    /// <summary>
    /// 요구받은 영역을 실제로 반영한다 — <b>얻는 것은 즉시, 잃는 것은 발밑을 비운 뒤.</b> (#744)
    ///
    /// <b>잃는 쪽을 미루는 이유</b>는 도로와 같다(<see cref="ApplyRoadPolicy"/>): 서 있는 폴리곤이
    /// 마스크 밖이 되면 경로 계산이 통째로 실패해(<c>PathInvalid</c>) 그 자리에서 굳는다. 도로와
    /// 다른 점은 굳는 자리가 <b>본부 실내나 셀</b>이라는 것이다 — 도로처럼 스스로 걸어 나올 수도 없다.
    ///
    /// <b>얻는 쪽까지 함께 미루면 안 된다.</b> 셀을 나설 때 Jail을 놓고 HQ를 받는데, 그 순간 몸은
    /// 아직 셀 안(Jail 위)이라 둘을 묶어 미루면 HQ도 안 열린 채 워프가 진행되고, 워프는 마스크를
    /// 보지 않으므로(<see cref="TryWarpNear"/>는 <c>AllAreas</c>로 붙인다) <b>못 걷는 폴리곤 위에
    /// 몸을 내려놓는다</b>.
    ///
    /// 이 구조라 <b>워프 실패도 저절로 수습된다</b> — 셀에 남으면 다음 확인에서 발밑이 여전히 Jail이라
    /// 그 통행이 계속 유지된다.
    /// </summary>
    private void ApplyGrantedAreas()
    {
        int losing = m_grantedAreas & ~m_requestedAreas;
        int keep = losing != 0 ? losing & NpcNavAreas.AreaMaskAt(transform.position) : 0;
        int next = m_requestedAreas | keep;

        if (next == m_grantedAreas)
            return;

        m_grantedAreas = next;

        // 지금 상태 기준으로 다시 건다 — 이 호출은 상태 전이 밖에서도 오므로(수감·방출) 여기서
        // 걸지 않으면 다음 전이까지 옛 마스크가 남는다.
        ApplyRoadPolicy(m_stateMachine.CurrentState);
    }

    /// <summary>
    /// 평소 시민 생활로 돌아오면 유치장 통행을 반납한다 — <see cref="NpcStateMachine.OnBeforeEnter"/>. (#744)
    ///
    /// <b>Idle·Walk가 기준인 이유</b>는 그 둘이 "이 사람의 볼일이 끝났다"는 유일한 공통 종착지라서다 —
    /// 탈옥 도주도, 반출 도주도, 잔류도 결국 여기로 가라앉는다. 방출 경로마다 회수를 배선하면
    /// 새 경로가 생길 때마다 빠뜨린다.
    ///
    /// 여기서는 "이제 필요 없다"만 알린다 — 실제로 언제 빠지는지는 <see cref="ApplyGrantedAreas"/>가 정한다.
    /// </summary>
    private void RevokeGrantedAreasOnCityLife(NpcState next)
    {
        if (next is NpcState.Idle or NpcState.Walk)
            SetGrantedAreas(0);
    }

    // 미뤄 둔 회수가 가능해졌는지 확인한다 — 서버(또는 오프라인) 전용. 도로 이탈과 같은 주기로 본다. (#744)
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

    // 도로 이탈 확인 주기(초) — 대기 중인 개체만, 그것도 간격을 두고 본다.
    // 매 프레임 NavMesh를 샘플하면 군중 규모에서 그대로 비용이 된다.
    private const float k_roadEgressProbeInterval = 0.25f;

    /// <summary>
    /// 상태에 맞는 통행 마스크를 건다 — <see cref="NpcStateMachine.OnBeforeEnter"/>에서 호출. (#634 후속)
    ///
    /// 도로를 밟으면 안 되는 상태인데 <b>지금 도로 위</b>라면 좁히지 않고 미룬다:
    /// 서 있는 폴리곤이 마스크 밖이 되면 경로 계산이 통째로 실패해(<c>PathInvalid</c>)
    /// <b>차도 한복판에서 영영 굳는다</b> — 고치려던 것보다 나쁜 증상이다.
    /// 미루는 동안에도 목적지 쪽은 이미 도로를 빼고 뽑으므로(<see cref="NpcWalkState"/>·
    /// <see cref="NpcSpawner"/>가 <c>NpcNavAreas.ExcludeRoad</c>를 쓴다) 스스로 도로를 벗어난다.
    /// </summary>
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
            SetAreaMask(BaseAreaMask); // 벗어날 때까지는 도로를 쓸 수 있어야 나갈 수 있다
            return;
        }

        m_roadEgressPending = false;
        SetAreaMask(NpcNavAreas.ExcludeRoad(BaseAreaMask));
    }

    /// <summary>
    /// 도로를 벗어나기를 기다렸다가 마스크를 좁힌다 — 서버(또는 오프라인) 전용. (#634 후속)
    /// 추격이 끝나 배회로 돌아온 NPC가 마침 차도 위였던 경우가 이 경로다.
    /// </summary>
    private void TickRoadEgress()
    {
        if (!m_roadEgressPending)
            return;

        m_roadEgressProbeSeconds += Time.deltaTime;
        if (m_roadEgressProbeSeconds < k_roadEgressProbeInterval)
            return;
        m_roadEgressProbeSeconds = 0f;

        // 그새 도로를 밟아도 되는 상태로 바뀌었다면 대기 자체가 무의미하다.
        // (ApplyRoadPolicy가 이미 껐겠지만, 전이 없이 여기까지 오는 경로가 생겨도 새지 않게 둔다)
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

    // 도로에서 물러날 거리(m) — <b>가장 가까운 도로 밖이 아니다.</b> 그건 경계선 바로 너머
    // 몇 cm라, 한 발짝 떼자마자 도착 판정이 나 NPC가 차도 경계에 붙어 선다(관측된 증상).
    // 폭 10m 도로 한복판에서 인도까지가 5m이므로(실측) 10m면 수직으로 나갈 때 5m 안쪽에 선다.
    private const float k_roadEgressDistance = 10f;

    // 목적지에 요구하는 도로 여유(m) — 이 반경 안에 도로가 없어야 "충분히 물러났다"고 본다.
    // <b>후보를 거리로 고르면 안 되는 이유가 여기 있다:</b> 후보는 전부 등거리라 거리로는
    // 우열이 안 갈리고, 실제로 대각선 후보가 뽑혀 여유 0.7m에 서는 것이 관측됐다.
    // 값은 실측 상한에 맞춘다 — 한복판에서 10m 수직 이동의 여유가 5m이므로 그보다 낮아야 한다.
    private const float k_roadEgressClearance = 2.5f;

    // 이탈 목적지 후보 방향 수 — 도로는 띠 모양이라 어느 쪽이 가까운 인도인지 모른다. 빙 둘러 보고
    // 도로 밖으로 나온 것 중 가장 가까운 것을 쓴다.
    private const int k_roadEgressDirections = 8;

    // 후보를 NavMesh에 붙일 때의 스냅 반경(m) — 넓히면 후보가 죄다 같은 지점으로 뭉친다.
    private const float k_roadEgressSnapRadius = 2f;

    /// <summary>
    /// 도로에서 <b>걸어 나가게 한다</b> — 기다리는 것만으로는 못 나오기 때문이다. (#634 후속)
    ///
    /// <b>Idle이 문제다.</b> 추격이 끝나면 <see cref="NpcDutyAgent.EndPenaltyDuty"/>가 Idle로
    /// 되돌리는데, <see cref="NpcIdleState"/>는 <c>isStopped = true</c>로 1~3초(15% 확률로 5~10초)
    /// 서 있는다. 그 자리가 차도 한복판이면 그 시간이 그대로 사망이다 — 실제로 관측된 증상이 이것이고,
    /// 마스크를 좁히지 못해 굳는 것과 <b>보이는 그림이 똑같아</b> 더 나쁘다.
    ///
    /// <b>그래서 정지를 덮어쓰는 게 아니라 상태를 옮긴다.</b> 에이전트만 밀면 FSM은 Idle인 채
    /// 몸만 이동해 <b>미끄러진다</b> — <see cref="NpcAnimationDriver"/>는 NpcState 값을 그대로
    /// Animator 번호로 쓰므로 Idle이면 속도와 무관하게 Idle 모션이 나온다. 걸어 나가는 중이면
    /// 그건 Walk다. 상태를 맞춰 두면 모션은 따라오고 드라이버는 손댈 필요가 없다.
    ///
    /// 목적지는 Walk가 스스로 뽑은 배회 지점이 아니라 <b>가장 가까운 도로 밖</b>으로 덮어쓴다 —
    /// 배회 지점은 반경 3~10m라 도로 폭(10m)을 넘어 건너편이 걸릴 수 있고, 차도 위에서 그건
    /// 가장 오래 걸리는 경로다.
    /// </summary>
    private void DriveOffRoad()
    {
        if (!AgentReady)
            return;

        // 이미 도로 밖을 향해 걷고 있으면 놔둔다 — 매 틱 목적지를 새로 잡으면 경로가 계속 리셋된다
        if (!m_agent.isStopped && m_agent.hasPath && !NpcNavAreas.IsOnRoad(m_agent.destination))
            return;

        // 나갈 곳을 먼저 찾는다 — 못 찾았는데 상태부터 옮기면 Idle↔Walk를 오가며 떨기만 한다
        if (!TryFindRoadExit(out Vector3 exit))
            return; // 다음 틱에 다시 시도한다

        // Walk로 옮긴 뒤 목적지를 덮는다 — 순서가 중요하다. ChangeState는 Enter()까지 돌고 오므로
        // (NpcWalkState.Enter가 자기 배회 지점을 잡는다) 먼저 걸면 그쪽이 이겨 버린다.
        if (m_stateMachine.CurrentState == NpcState.Idle)
            m_stateMachine.ChangeState(NpcState.Walk);

        m_agent.isStopped = false;
        m_agent.SetDestination(exit);
    }

    /// <summary>
    /// 도로를 벗어나 <b>충분히 안쪽</b>에 있는 지점을 찾는다 — 빙 둘러 보고 고른다.
    ///
    /// 가장 가까운 도로 밖 지점(<c>SamplePosition</c> 한 번)으로는 안 된다: 그건 경계선 바로 너머라
    /// 한 발짝 만에 도착 판정이 나고, NPC가 차도 경계에 붙어 선 채 Idle로 돌아간다.
    ///
    /// 후보 사이의 우열은 <b>거리가 아니라 도로 여유</b>로 가른다 — 후보는 전부 등거리라 거리로는
    /// 갈리지 않고, 그렇게 두면 도로를 비스듬히 스치는 대각선 후보가 뽑힌다.
    /// 여유를 갖춘 후보가 하나도 없으면(좁은 골목 등) 도로 밖이기만 한 후보라도 쓴다 —
    /// 차도에 서 있는 것보다는 언제나 낫다.
    /// </summary>
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

            // 스냅이 도로로 되돌아온 후보는 버린다 — 마스크로 걸러도 경계에 걸치면 다시 도로다
            if (NpcNavAreas.IsOnRoad(hit.position))
                continue;

            float sqr = (hit.position - origin).sqrMagnitude;

            // 도로에서 충분히 떨어졌는가 — 반경 안에 도로가 <b>없어야</b> 한다.
            // 여기서는 Road 마스크로 직접 샘플하는 것이 맞다: 묻는 것이 "이 폴리곤이 도로인가"가
            // 아니라 "이 근처에 도로가 있는가"이기 때문이다 (NpcNavAreas.IsOnRoad와 반대다).
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

    /// <summary>
    /// 통행 마스크를 갈아 끼우고, 바뀌었으면 <b>지금 경로를 다시 계산시킨다.</b>
    ///
    /// <c>areaMask</c>를 바꿔도 이미 계산된 경로는 그대로 남는다 — 도로를 지나는 옛 경로가 살아 있으면
    /// 좁힌 의미가 없다. 다시 계산해 부분 경로가 나오면 각 상태의 막힘 감지가 목적지를 새로 뽑는다
    /// (<see cref="NpcWalkState"/>는 제자리 2초로 잡는다).
    /// </summary>
    private void SetAreaMask(int mask)
    {
        if (m_agent.areaMask == mask)
            return;

        m_agent.areaMask = mask;

        if (AgentReady && m_agent.hasPath)
            m_agent.SetDestination(m_agent.destination);
    }

    // ---- 굳은 몸 회수 (#557) ----

    // 회수를 걸기까지의 유예(초) — 기절 시간(NpcStunConfig.StunSeconds)보다 짧아야 ExitStun보다 먼저
    // 붙어 깨어나는 경로(isStopped 복구 → StartFlee)가 이어진다. 한 프레임짜리 이탈까지 잡으면
    // 정상 경로의 워프와 겹쳐 몸이 두 번 튄다.
    private const float k_stuckGraceSeconds = 1f;

    // 회수용 탐색 반경(m) — 기본 반경으로 못 붙였을 때의 최후 수단. 8m은 실측이다: 맵 <b>안쪽</b>에서
    // 설 수 있는 지면 중 NavMesh가 2m 안에 없는 곳은 HQ 실내뿐이고(최대 5.5m) 야외는 전 구간 2m 안이다.
    // 더 넓혀도 맵 밖으로 나간 몸(#559)에는 어차피 닿지 않고, 엉뚱한 곳으로 튕겨 나갈 위험만 커진다.
    private const float k_stuckRecoverRadius = 8f;

    private float m_offNavMeshSeconds;

    // 회수 실패를 이미 알렸는가 — 재시도는 계속하되 로그는 굳은 구간당 한 번만. 반경 밖까지 밀려나는
    // 경로가 실제로 있어(#559 — 납치 반출이 맵 밖 25m까지 끌고 나간다) 매초 LogError면 콘솔을 덮는다.
    private bool m_stuckReported;

    /// <summary>
    /// <b>에이전트가 켜져 있는데 NavMesh 밖</b>인 상태를 서버가 스스로 회수한다. 서버(또는 오프라인) 전용. (#557)
    ///
    /// 이 상태를 만드는 넷(밧줄 놓기·넉백 착지·기절 해제·<b>래그돌 기상</b>)이 전부 실패 시 경고만
    /// 남기고 포기해, 이후 <c>isStopped</c>·<c>SetDestination</c>이 조용히 실패하며 NPC가 굳었다
    /// (빌드 2 이슈 E의 재발). 호출부마다 폴백을 다는 대신 <b>결과 상태 하나</b>를 여기서 보면
    /// 늘어날 호출부까지 덮인다.
    ///
    /// 에이전트를 꺼 둔 구간(넉백 비행·밧줄 끌기·<b>래그돌</b>)은 위치를 그쪽이 쥐고 있어 굳은 것이
    /// 아니다 — 건너뛴다.
    ///
    /// ⚠ <b>그래서 꺼 둔 쪽은 반드시 스스로 켜야 한다.</b> 이 회수는 <c>enabled == true</c>인데
    /// NavMesh 밖인 경우만 잡으므로, 꺼 놓고 아무도 켜지 않으면 회수가 <b>영영 오지 않는다.</b>
    /// 켜는 것은 이 함수의 <b>전제</b>이지 생략해도 되는 이유가 아니다
    /// (<see cref="NpcRagdoll"/>의 기상이 실패해도 에이전트를 켜 두는 이유가 이것이다, #572).
    /// </summary>
    private void TickNavMeshRecovery()
    {
        if (!m_agent.enabled || m_agent.isOnNavMesh)
        {
            m_offNavMeshSeconds = 0f;
            m_stuckReported = false; // 다음에 또 굳으면 그때는 다시 알린다
            return;
        }

        m_offNavMeshSeconds += Time.deltaTime;
        if (m_offNavMeshSeconds < k_stuckGraceSeconds)
            return;

        m_offNavMeshSeconds = 0f; // 실패해도 유예를 다시 채워 매 프레임이 아니라 매 1초로 재시도한다

        Vector3 from = transform.position;
        if (!TryWarpNear(from, k_stuckRecoverRadius))
        {
            // 재시도는 이어진다 — 몸이 다시 끌려 들어오면(밧줄 등) 그때 붙는다. 알림만 한 번이다.
            if (!m_stuckReported)
            {
                m_stuckReported = true;
                Debug.LogError(
                    $"NpcController: NavMesh 밖에서 굳은 NPC를 {k_stuckRecoverRadius}m 안에서 회수하지 "
                        + $"못했다 — 재시도는 계속한다: {name} @{from.ToString("F1")}",
                    this
                );
            }

            return;
        }

        // 회수 사실 자체가 원인 추적의 유일한 단서다 — "무엇이 밖으로 밀어냈는가"는 아직 미확인이다
        Debug.LogWarning(
            $"NpcController: NavMesh 밖에서 굳은 NPC를 회수했다 — {name} "
                + $"{from.ToString("F1")} → {transform.position.ToString("F1")}",
            this
        );
    }
}
