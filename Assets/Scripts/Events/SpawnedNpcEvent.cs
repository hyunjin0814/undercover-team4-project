using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰형 돌발 이벤트 1종 — 현장 근처에 NPC를 스폰해 소란을 일으키고, 검거 판정 또는 잔류 전환으로 종료한다. (GDD 6-4/7-4, #106)
/// 데이터(모드·프리팹)로 두 변형을 구성한다:
///  · <b>거리 난동자</b>(<see cref="Behavior.Resist"/>) — 그 자리에서 저항하며 소란·근접 HP 타격, 제압 대상.
///  · <b>나체(속옷) 난동꾼</b>(<see cref="Behavior.Flee"/>) — 플레이어에게서 도주하며 뛰어다녀 소란, 쫓아가 제압.
/// 둘 다 기존 <see cref="NpcController"/> 로직(Attack/Run 상태의 소란 펄스 #81, 상호작용키 E 제압)을 그대로 재사용한다.
/// 제압은 종료가 아니라 시작이다 — 스폰 시 <see cref="MisdemeanorOffender"/> 마커를 붙여 두면 일반 용의자와 똑같이
/// "제압 → 수갑 → E로 연행 → HQ 인계" 흐름을 타고, <see cref="ArrestJudge"/>가 경범죄로 판정하며 수익도 그쪽에서 지급한다.
/// 판정된 신병은 CustodyRouter가 유치장으로 이송한다(경범죄 수감 — 2026-07-23 확정) — 이 이벤트는 판정
/// 시점에 추적을 끊고 뒷정리(라운드 종료)를 <see cref="MisdemeanorLoiterer"/>에 넘기는 것까지만 맡는다.
/// 스폰·판정은 서버(또는 오프라인)에서만 — 스폰물은 NetworkObject로 복제된다. (#56)
///
/// <b>컴포넌트가 아니라 데이터다</b> — <see cref="SpawnedNpcEventSet"/>의 인스펙터 리스트 항목으로 존재하며,
/// 종류를 늘리려면 항목을 추가하면 된다(코드 수정 불필요). MonoBehaviour가 아니므로 수명주기 훅이 없고,
/// Set이 <see cref="Initialize"/>·<see cref="Subscribe"/>·<see cref="Unsubscribe"/>를 대신 돌려준다.
/// </summary>
[System.Serializable]
public class SpawnedNpcEvent : ISuddenEvent
{
    /// <summary>스폰 직후 NPC가 취할 행동. Resist=그 자리 저항(난동자), Flee=플레이어에게서 도주(난동꾼).</summary>
    public enum Behavior { Resist, Flee }

    [Header("이벤트 정의")]
    [Tooltip("로그·HUD에 표시할 이름 (예: 거리 난동자 / 나체 난동꾼)")]
    [SerializeField] private string m_displayName = "거리 난동자";
    [Tooltip("Resist=그 자리에서 저항(거리 난동자) / Flee=플레이어에게서 도주하며 뛰어다님(나체 난동꾼)")]
    [SerializeField] private Behavior m_mode = Behavior.Resist;

    [Header("스폰 NPC 프리팹")]
    [SerializeField] private NpcController m_npcPrefab;

    [Header("스폰 위치 — 현장 플레이어 기준 거리(m)")]
    [Tooltip("무작위로 고른 현장 플레이어에서 이 범위(min~max) 안에 스폰한다")]
    [SerializeField] private float m_spawnDistanceMin = 6f;
    [SerializeField] private float m_spawnDistanceMax = 12f;
    [Tooltip("스폰 후보 지점에서 이 거리(m) 안에 NavMesh가 없으면 그 지점은 버린다")]
    [SerializeField] private float m_navSampleMaxDistance = 4f;
    [Tooltip("유효한 스폰 지점을 찾는 최대 시도 횟수")]
    [SerializeField] private int m_maxSpawnAttempts = 8;

    [Header("경범죄 수익")]
    [Tooltip("본부 인계 후 경범죄 판정 성공 시의 수익 하한 — 스폰 시점에 [하한, 상한]에서 100원 단위로 뽑아 마커에 박는다 (#395)")]
    [Min(0)]
    [SerializeField] private int m_pettyCrimeRewardMin = 500;

    [Tooltip("경범죄 수익 상한. 하한보다 작으면 하한이 쓰인다")]
    [Min(0)]
    [SerializeField] private int m_pettyCrimeRewardMax = 4000;

    // 이번 스폰에서 실제로 뽑힌 수익 — 마커에 실은 값과 같다 (#395)
    private int m_rolledReward;

    [Header("소란 지속")]
    [Tooltip("제압되지 않은 채 이 시간(초)이 지나면 소란을 멈추고 진정해 배회 시민으로 잔류한다 — 마커가 남아 언제든 잡으면 경범죄 수익 (#310)")]
    [SerializeField] private float m_maxLifetimeSeconds = 60f;

    // Set이 주입하는 런타임 참조 — 직렬화되지 않는다.
    // m_owner는 Debug 로그의 컨텍스트 오브젝트로 쓴다(순수 클래스라 `this`를 넘길 수 없다).
    private MonoBehaviour m_owner;
    private ArrestJudge m_arrestJudge;

    private NpcController m_npc;
    private Transform m_threat;  // 스폰 기준이 된 플레이어 — 도주(Flee)형이 달아날 대상으로 쓴다
    private float m_startTime;
    private int m_spawnFrame;
    private bool m_pendingStart;  // 스폰 다음 프레임에 행동을 적용(초기화 순서 보장)하기 위한 플래그
    private bool m_hasStarted;    // 행동을 실제로 시작했는지 — 이탈(배회 복귀) 종료 판정에 쓴다
    private bool m_captured;      // 한 번이라도 제압됐는지 — 제압 로그를 첫 진입에만 남기려고 쓴다
    private bool m_releaseQueued; // 잔류 전환 확정 — 다음 틱에 이벤트가 손을 뗀다 (상태 전이 체인 안 처리 회피, #310)

    public string DisplayName => m_displayName;

    public bool IsActive => m_npc != null;

    /// <summary>Set이 Awake에서 1회 호출 — 로그 컨텍스트와 공용 <see cref="ArrestJudge"/>를 주입한다.</summary>
    public void Initialize(MonoBehaviour owner, ArrestJudge arrestJudge)
    {
        m_owner = owner;
        m_arrestJudge = arrestJudge;
    }

    /// <summary>Set이 OnEnable에서 호출 — 검거 판정 구독. (MonoBehaviour가 아니라 훅이 없다)</summary>
    public void Subscribe()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged += HandleArrestJudged;
    }

    /// <summary>Set이 OnDisable에서 호출 — 구독 해제.</summary>
    public void Unsubscribe()
    {
        if (m_arrestJudge != null)
            m_arrestJudge.OnArrestJudged -= HandleArrestJudged;
    }

    public bool CanTrigger()
    {
        // 소란을 일으킬 현장 플레이어가 있어야 성립한다
        return SuddenEventUtil.FindRandomFieldPlayer() != null;
    }

    public void ServerBegin()
    {
        if (m_npcPrefab == null)
        {
            Debug.LogWarning($"SpawnedNpcEvent({m_displayName}): NPC 프리팹이 지정되지 않음", m_owner);
            return;
        }

        Transform player = SuddenEventUtil.FindRandomFieldPlayer();
        if (player == null)
            return; // 발생 직전에 대상이 사라짐 — 이번엔 건너뛴다 (IsActive=false 유지)

        if (!SuddenEventUtil.TryFindSpawnPositionNear(
                player.position, m_spawnDistanceMin, m_spawnDistanceMax, m_navSampleMaxDistance, m_maxSpawnAttempts,
                out Vector3 spawnPosition, hiddenFromPlayers: true)) // 눈앞 팝인 방지 (#332 A)
        {
            Debug.LogWarning($"SpawnedNpcEvent({m_displayName}): NavMesh 위 스폰 지점을 찾지 못해 발생 취소", m_owner);
            return;
        }

        Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        m_npc = UnityEngine.Object.Instantiate(m_npcPrefab, spawnPosition, rotation);

        // 경범죄 표식 — 인계되면 ArrestJudge가 이 마커를 보고 진범 대조 대신 경범죄로 판정하고 Reward를 지급한다 (#106).
        // 판정이 서버 권위라 마커도 서버에서만 읽힌다 — 복제할 필요가 없어 plain MonoBehaviour로 붙인다.
        // 소란 행동도 함께 기록한다 — 탈옥으로 방출되면 이 행동을 재개한다 (MisdemeanorLoiterer.BeginRiot).
        MisdemeanorOffender offender = m_npc.gameObject.AddComponent<MisdemeanorOffender>();
        // 수익은 스폰 시점에 확정한다 (#395) — 판정 시점에 뽑으면 재검거로 금액을 리롤할 수 있다.
        // 뽑은 값을 따로 들고 있는 이유는 아래 제압 로그가 실제 지급될 금액을 보여주기 위함이다.
        m_rolledReward = BountyRoll.Roll(m_pettyCrimeRewardMin, m_pettyCrimeRewardMax);
        offender.Reward = m_rolledReward;
        offender.SetRiotBehavior(m_mode, m_maxLifetimeSeconds);

        // 네트워크 세션이면 전 클라에 복제 — Spawn()이 서버에서 OnNetworkSpawn(InitBehavior)를 동기 실행한다 (#56)
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_npc.GetComponent<NetworkObject>().Spawn();

        m_npc.OnStateChanged += HandleStateChanged;
        m_threat = player;
        m_startTime = Time.time;
        m_spawnFrame = Time.frameCount;
        m_pendingStart = true;
        m_hasStarted = false;
        m_captured = false;
        m_releaseQueued = false;
    }

    public void ServerTick()
    {
        if (m_npc == null)
            return;

        // 스폰 초기화(InitBehavior의 Idle 전환)가 끝난 다음 프레임에 행동을 적용한다 —
        // 같은 프레임에 부르면 뒤이어 실행되는 InitBehavior가 Idle로 덮어쓸 수 있다.
        if (m_pendingStart && Time.frameCount > m_spawnFrame)
        {
            ApplyBehavior();
            m_pendingStart = false;
            m_hasStarted = true;
        }

        // 잔류 전환 확정분을 상태 전이 체인 밖(다음 틱)에서 처리한다 — OnStateChanged 안에서 곧바로
        // 상태를 갈아타면 전이 통지가 중첩된다. (#310)
        if (m_releaseQueued)
        {
            ReleaseToCity();
            return;
        }

        // 연행 중에는 소란 타이머를 멈춘다 — 본부까지 데려가는 동안 이벤트가 끝나면 안 된다.
        if (m_npc.CurrentState == NpcState.Escorted)
            m_startTime = Time.time;

        // 소란 지속 시간이 다하면 진정하고 배회 시민으로 잔류한다 — 저항형(Resist)은 스스로 멈추지 않으므로
        // 이 타이머가 소란의 끝이다. 제압 시점에 타이머를 새로 돌리므로 "제압해 놓고 안 데려간" 경우도
        // 같은 유예 뒤 (수갑이 풀려 배회 복귀 →) 잔류로 넘어간다.
        if (Time.time - m_startTime > m_maxLifetimeSeconds)
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 소란 지속 시간 종료, 진정");
            m_npc.StartFlee(null); // 위협 없는 도주 — 잠깐 흩어졌다가 곧 배회(Idle)로 가라앉는다
            ReleaseToCity();
        }
    }

    public void ServerReset()
    {
        Despawn(playVfx: false); // 라운드 종료 일괄 정리 — 남은 스폰물마다 이펙트가 터지지 않게 연출은 끈다
    }

    // 모드에 따라 스폰 직후 행동을 적용한다 (서버에서만 호출됨)
    private void ApplyBehavior()
    {
        switch (m_mode)
        {
            case Behavior.Resist:
                // 그 자리에서 버티며 저항 — 제압 대상(E로 제압)이자 소란원
                m_npc.StartResist();
                break;

            case Behavior.Flee:
                // 위협(플레이어)에게서 도주하며 뛰어다녀 소란을 퍼뜨린다. 대상이 없으면 배회로 두어 곧 이탈 종료.
                if (m_threat != null)
                    m_npc.StartFlee(m_threat);
                break;
        }
    }

    // 상태 전이 수신 — 제압(Captured)은 연행 대기, 행동 시작 뒤 배회 복귀(Idle/Walk)는 이탈로 종료 처리
    private void HandleStateChanged(NpcState state)
    {
        if (m_npc == null)
            return;

        if (state == NpcState.Captured)
        {
            // 제압만으로는 아무 일도 일어나지 않는다 — 본부까지 연행해야 판정·수익이 난다.
            // 연행이 끊겨 다시 Captured로 돌아온 경우에도 방치 유예를 새로 준다.
            m_startTime = Time.time;
            if (!m_captured)
            {
                m_captured = true;
                Debug.Log($"[돌발이벤트] {m_displayName} 제압 — 본부로 연행하면 경범죄 처리(수익 {m_rolledReward})");
            }
            return;
        }

        // 행동을 시작한 뒤 배회 상태로 돌아왔다 = 제압 실패로 뿌리치고 이탈함. 소멸시키지 않고
        // 배회 시민으로 도심에 남긴다 (#310) — 마커가 남아 있어 언제든 다시 잡아 인계하면 수익이 난다.
        if (m_hasStarted && (state == NpcState.Idle || state == NpcState.Walk))
        {
            Debug.Log($"[돌발이벤트] {m_displayName} — 제압 실패, 도심에 잔류");
            m_releaseQueued = true;
        }
    }

    // 검거 판정 수신 — 수감(유치장 이송)은 CustodyRouter가 하므로, 이벤트는 추적만 끊는다.
    // 수익은 ArrestJudge가 이미 지급했다(첫 판정 한정). 뒷정리(라운드 종료)는 Loiterer가 물려받는다.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_npc == null || result.Npc != m_npc)
            return;

        Debug.Log($"[돌발이벤트] {m_displayName} — 경범죄 판정, 유치장 인계 (이벤트 종료)");
        ReleaseToCity();
    }

    // 이벤트가 손을 떼고 NPC를 도심에 남긴다 — 뒷일(인계 판정·라운드 종료 정리)은 MisdemeanorLoiterer가
    // 물려받고, 이 이벤트는 비활성(IsActive=false)이 되어 같은 종류가 새로 추첨될 수 있다. (#310)
    private void ReleaseToCity()
    {
        NpcController npc = m_npc;

        npc.OnStateChanged -= HandleStateChanged;
        m_npc = null;
        m_threat = null;
        m_pendingStart = false;
        m_hasStarted = false;
        m_captured = false;
        m_releaseQueued = false;

        MisdemeanorLoiterer.Attach(npc, m_displayName);
    }

    // 스폰한 NPC를 정리한다 — 구독 해제 후 Despawn/Destroy하고 참조·플래그를 비운다.
    private void Despawn(bool playVfx = true)
    {
        if (m_npc == null)
            return;

        m_npc.OnStateChanged -= HandleStateChanged;

        // 연행 중인 채로 정리되면(라운드 종료 등) 연행 참조가 파괴된 NPC를 가리킨 채 남아 그 플레이어가
        // 영영 연행 중이 된다 — 파괴 전에 놓게 한다. 판정 경로에서는 ArrestJudge가 이미 놓았으므로 null이다.
        // 줄다리기로 여러 명이 걸려 있을 수 있다 — 전원에게서 이 대상의 줄만 뺀다 (#390).
        foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(m_npc))
            escorter.ReleaseDrag(m_npc);

        SuddenEventUtil.DespawnOrDestroy(m_npc.gameObject, playVfx);

        m_npc = null;
        m_threat = null;
        m_pendingStart = false;
        m_hasStarted = false;
        m_captured = false;
        m_releaseQueued = false;
    }
}
