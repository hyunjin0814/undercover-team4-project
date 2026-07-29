using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 범인 탈출 (돌발 이벤트 · 본부) — 침입자가 본부에 들어와 유치장 자물쇠를 열고,
/// 수감돼 있던 범인들을 탈출시킨다. (GDD 6-4, #231/#261)
/// 본부 무인 조건은 #311에서 제거됐다(2026-07-23 확정) — 수감자만 있으면 언제든 발동할 수 있어,
/// 본부에 있어도 침입자를 알아채고 막아야 한다.
///
/// <b>대응 구간이 둘 있다</b> (#261):
///  · 이동 구간 — 침입자는 일반 NPC와 같은 스폰 포인트에서 나와 자물쇠까지 걸어온다. 겉모습·출신지가
///    시민과 구분되지 않으므로, 본부로 곧장 향하는 걸음을 알아채는 것이 유일한 단서다(조용히 발생 —
///    <see cref="AnnounceOnBegin"/>가 false인 이유).
///  · 해제 구간 — 자물쇠에 닿으면 그때 경보를 울리고 m_unlockSeconds 동안 해제를 진행한다. 늦게 알아챈
///    팀도 달려와 막을 수 있는 마지막 기회다.
/// 어느 구간이든 수갑을 채우면 침입자는 저항형으로 맞서고, 제압·연행해 인계하면 경범죄로 처리된다
/// (<see cref="MisdemeanorOffender"/> 마커 — 진범 대조를 타지 않으므로 오검거가 아니다).
/// 판정된 신병은 CustodyRouter가 유치장으로 이송한다 — 경범죄 수감 확정(2026-07-23)으로 #299의
/// '유치장은 진범 전용' 규칙은 폐기됐다. 수감된 침입자가 또 탈옥으로 풀려날 수 있지만(수감자 존재
/// 조건 충족), 반복 수익은 ArrestJudge가 첫 판정 후 마커 보상을 비워 막는다.
///
/// 흐름(전부 서버 권위 · #56):
///  1. <see cref="CanTrigger"/> — 자물쇠 잠김 + 수감자 존재일 때 성립 (본부 무인 조건은 #311에서 제거).
///  2. <see cref="ServerBegin"/> — 침입자 NPC를 도시 스폰 포인트에 스폰(다음 프레임에 StartIntrude).
///  3. 해제 착수(OnIntrudeUnlockStarted) — 본부 경보를 울린다.
///  4. 해제 완료(OnIntrudeFinished reached=true) — 자물쇠를 열고 수감자를 전원 방출한다.
///     · 방출: JailZone.ReleaseInmate + NpcController.ClearDelivered + StartFlee(재검거 가능하게)
///     · 진범만: RoundManager.ReportCriminalEscaped + WantedListManager.ReinstateByNpcId
///  5. 침입자도 함께 달아난다 — 추격해 잡으면 경범죄 수익은 챙길 수 있다. 방치되면 수명 초과로 정리.
///
/// 발동 빈도(추첨 주기)는 <see cref="SuddenEventManager"/>가 쥐고, 이 이벤트는 "지금 발동 가능한가"만 판정한다.
/// 스폰물(침입자)은 자기 NetworkObject로, 자물쇠·수배·할당량 상태는 각 소유 컴포넌트가 전파한다 —
/// 이 이벤트는 매니저처럼 상태를 얹지 않는다(ISuddenEvent 규약).
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

    // 매니저는 캐싱하지 않고 App 경유로 매번 읽는다 (아키텍처 규칙 R1/R8).
    // 침입자 스폰 지점은 일반 NPC와 같아야 하므로 NpcSpawner의 것을 빌려 쓴다.
    private NpcSpawner Spawner => App.Game.NpcSpawner;
    private WantedListManager WantedList => App.Game.WantedList;
    private RoundManager Round => App.Game.Round;
    private ArrestJudge Judge => App.Game.ArrestJudge;
    private SuddenEventManager SuddenEvents => App.Game.SuddenEvent;

    private NpcController m_intruder;
    private bool m_pendingStart;  // 스폰 다음 프레임에 침입을 시작하기 위한 플래그(초기화 순서 보장)
    private bool m_hasStarted;    // 침입을 실제로 시작했는지 — 배회 복귀(이탈) 판정에 쓴다
    private bool m_releaseQueued; // 잔류 전환 확정 — 다음 틱에 이벤트가 손을 뗀다 (상태 전이 체인 안 처리 회피, #310)
    private int m_spawnFrame;
    private float m_lifetimeStart; // 방치 타이머 기준 시각 — 국면이 바뀔 때마다 갱신한다

    // 방출 대상 스냅샷 — Inmates(HashSet 뷰)를 순회하며 ReleaseInmate로 수정하면 열거 예외가 나므로 복사한다
    private readonly List<NpcController> m_releaseBuffer = new List<NpcController>();

    public string DisplayName => "범인 탈출";

    public bool IsActive => m_intruder != null;

    /// <summary>조용히 시작한다 — 침입자가 자물쇠에 손댈 때까지 알리지 않아야 이동 구간이 관찰 대상이 된다. (#261)</summary>
    public bool AnnounceOnBegin => false;

    private void Awake()
    {
        // 매니저가 아닌 장소·부품만 여기서 찾는다 (자물쇠·유치장).
        // 매니저는 App 경유 프로퍼티로 읽으므로 Awake에서 손대지 않는다 — 등록이 아직 안 끝났을 수 있다.
        if (m_jailZone == null)
            m_jailZone = FindFirstObjectByType<JailZone>();
        if (m_jailLock == null)
            m_jailLock = m_jailZone != null ? m_jailZone.GetComponent<JailLock>() : FindFirstObjectByType<JailLock>();
    }

    // 매니저 구독은 Start에서 — 모든 매니저의 Awake(=App 등록)가 끝난 뒤가 보장된다 (아키텍처 규칙 R6).
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

        // 자물쇠가 아직 잠겨 있고, 풀어 줄 수감자가 실제로 있어야 성립한다 — 빈 유치장에서
        // 자물쇠만 여는 무의미 발동을 막는다. 본부 무인 조건은 #311에서 제거 — 본부에 있어도
        // 침입자를 알아채고 저지해야 하는 상시 위협이 됐다.
        // (자물쇠는 새 수감자가 들어올 때 JailZone.Admit이 다시 잠그므로 연속 발동은 자연히 막힌다)
        if (!m_jailLock.IsLocked)
            return false;
        if (m_jailZone.InmateCount <= 0)
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

        // 경범죄 표식 — 인계되면 ArrestJudge가 진범 대조 대신 경범죄로 판정하고 Reward를 지급한다 (#106).
        // 침입자는 CriminalAssigner를 타지 않아 IsCriminal이 false다. 이 마커가 없으면 침입을 막은 플레이어가
        // 오검거 페널티를 먹는다 — 대응에 성공한 쪽이 손해 보는 판정을 막는 것이 이 한 줄의 역할이다. (#261)
        // 수익은 스폰 시점에 확정한다 (#395) — 판정 시점에 뽑으면 재검거로 금액을 리롤할 수 있다
        m_intruder.gameObject.AddComponent<MisdemeanorOffender>().Reward =
            BountyRoll.Roll(m_intruderRewardMin, m_intruderRewardMax);

        if (SuddenEventUtil.IsNetworkSessionActive)
            m_intruder.GetComponent<NetworkObject>().Spawn();

        // 해제 착수·완료 통보를 받아 경보/자물쇠 해제를, 상태 전이를 받아 플레이어 개입을 처리한다
        m_intruder.OnIntrudeUnlockStarted += HandleUnlockStarted;
        m_intruder.OnIntrudeFinished += HandleIntrudeFinished;
        m_intruder.OnStateChanged += HandleStateChanged;

        m_hasStarted = false;
        m_releaseQueued = false;
        m_spawnFrame = Time.frameCount;
        m_pendingStart = true;
        m_lifetimeStart = Time.time;
    }

    public void ServerTick()
    {
        if (m_intruder == null)
            return;

        // 스폰 초기화(InitBehavior의 Idle 전환)가 끝난 다음 프레임에 침입을 시작한다 —
        // 같은 프레임에 부르면 뒤이어 실행되는 InitBehavior가 Idle로 덮어쓸 수 있다.
        if (m_pendingStart && Time.frameCount > m_spawnFrame)
        {
            m_intruder.StartIntrude(m_jailLock.ApproachPoint, m_unlockSeconds);
            m_pendingStart = false;
            m_hasStarted = true;
        }

        // 잔류 전환 확정분을 상태 전이 체인 밖(다음 틱)에서 처리한다 — OnStateChanged 안에서 곧바로
        // 상태를 갈아타면 전이 통지가 중첩된다 (SpawnedNpcEvent와 같은 이유). (#310)
        if (m_releaseQueued)
        {
            ReleaseToCity();
            return;
        }

        // 연행 중에는 잔류 타이머를 멈춘다 — 본부까지 데려가는 동안 이벤트가 끝나면 안 된다.
        // (이 리셋이 없으면 제압한 침입자가 연행 도중 잔류 전환돼 이벤트 추적이 끊긴다 — #261에서 고친 버그의 변형)
        if (m_intruder.CurrentState == NpcState.Escorted)
            m_lifetimeStart = Time.time;

        // 잔류 전환 시간이 다하면 침입을 포기하고 배회 시민으로 잔류한다 (SpawnedNpcEvent와 동일 설계).
        if (Time.time - m_lifetimeStart > m_maxLifetimeSeconds)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 침입 포기, 잔류");
            m_intruder.StartFlee(null); // 위협 없는 도주 — 잠깐 흩어졌다가 곧 배회로 가라앉는다
            ReleaseToCity();
        }
    }

    public void ServerReset()
    {
        // 라운드 종료 등으로 즉시 끝난다 — 침입자만 정리한다.
        // 이미 열린 자물쇠·방출된 수감자는 되돌리지 않는다: 라운드가 끝났으므로 의미가 없고,
        // 새 라운드 준비 시 유치장/자물쇠가 스스로 초기화된다. 일괄 정리라 소멸 연출은 끈다.
        Despawn(playVfx: false);
    }

    // 일반 NPC와 같은 스폰 포인트를 무작위로 골라 그 주변 NavMesh 위 지점을 찾는다 (#261).
    // 분산 반경 안에서 다시 뽑는 방식이라 같은 포인트라도 매번 다른 자리에서 나온다.
    private bool TryFindSpawnPosition(out Vector3 result)
    {
        IReadOnlyList<Transform> points = Spawner != null ? Spawner.SpawnPoints : null;
        if (points == null || points.Count == 0)
        {
            result = default;
            return false;
        }

        // 무작위 지점에서 시작해 목록을 한 바퀴 돈다 — 고른 포인트가 비어 있거나(인스펙터 미설정)
        // 주변에 NavMesh가 없어도 이벤트를 통째로 취소하지 않고 다음 포인트로 넘어간다 (JailZone.ReserveCell과 같은 방식).
        int start = Random.Range(0, points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Transform point = points[(start + i) % points.Count];
            if (point == null)
                continue;

            if (SuddenEventUtil.TryFindSpawnPositionNear(
                    point.position, 0f, m_spawnRadius, m_navSampleMaxDistance, m_maxSpawnAttempts, out result))
                return true;
        }

        result = default;
        return false;
    }

    // 자물쇠 해제 착수 — 이 순간 본부 경보를 울린다. 발동 시점에는 알리지 않았으므로(AnnounceOnBegin=false)
    // 팀이 침입을 처음 인지하는 지점이 여기다. 연출(HUD·사운드)은 OnEventAnnounced 구독으로 붙인다 (#43).
    private void HandleUnlockStarted(NpcController npc)
    {
        if (npc != m_intruder)
            return;

        Debug.Log($"[돌발이벤트] 범인 탈출 — 자물쇠 해제 시작, {m_unlockSeconds}초 후 개방");
        if (SuddenEvents != null)
            SuddenEvents.Announce(DisplayName);

        // 전 플레이어 팝업 — 대응 구간이 시작됐음을 알린다 (#311)
        m_jailLock.ServerAnnounceUnlockAttempt();
    }

    // 해제 완료 — 자물쇠를 열고 수감자를 방출한다. 경로 실패면 불발로 정리한다.
    private void HandleIntrudeFinished(NpcController npc, bool reached)
    {
        if (npc != m_intruder)
            return;

        m_intruder.OnIntrudeFinished -= HandleIntrudeFinished;
        m_intruder.OnIntrudeUnlockStarted -= HandleUnlockStarted;

        if (!reached)
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입 경로 실패, 불발 정리");
            Despawn();
            return;
        }

        m_jailLock.ServerUnlock();
        ReleaseAllInmates();

        // 침입자도 수감자들과 함께 달아난다 — 늦게 도착한 팀도 추격해 잡으면 경범죄 수익은 챙길 수 있다.
        // 방치 유예를 새로 줘서 도주 직후 강제 정리로 증발하지 않게 한다.
        m_lifetimeStart = Time.time;
        m_intruder.StartFlee(null);
    }

    // 상태 전이 수신 — 플레이어 개입(제압·연행)은 유예 갱신, 배회 복귀는 이탈로 보고 정리한다.
    private void HandleStateChanged(NpcState state)
    {
        if (m_intruder == null)
            return;

        // 제압·연행 중에는 유예를 새로 준다. 침입이 아직 진행 중이었다면 이 전이가 곧 "저지 성공"이다 —
        // 침입자는 죽이지 않는다(플레이어가 연행 중일 수 있다). 이후 수명은 방치 타이머가 관리한다.
        if (state == NpcState.Captured || state == NpcState.Escorted)
        {
            m_lifetimeStart = Time.time;
            return;
        }

        // 침입을 시작한 뒤 배회로 돌아왔다 = 뿌리치고 달아나 진정했거나(저지 실패) 도주가 끝났다.
        // 소멸시키지 않고 배회 시민으로 도심에 남긴다 (#310) — 마커가 남아 언제든 잡아 인계하면 수익이 난다.
        if (m_hasStarted && (state == NpcState.Idle || state == NpcState.Walk))
        {
            Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 도심에 잔류");
            m_releaseQueued = true;
        }
    }

    // 검거 판정 수신 — 수감(유치장 이송)은 CustodyRouter가 하므로, 이벤트는 추적만 끊는다.
    // 수익은 ArrestJudge가 이미 지급했다(첫 판정 한정). 뒷정리(라운드 종료)는 Loiterer가 물려받는다.
    private void HandleArrestJudged(ArrestResult result)
    {
        if (m_intruder == null || result.Npc != m_intruder)
            return;

        Debug.Log("[돌발이벤트] 범인 탈출 — 침입자 경범죄 판정, 유치장 인계 (이벤트 종료)");
        ReleaseToCity();
    }

    // 수감자를 전원 방출한다 — 자물쇠가 열린 순간 모두 뛰쳐나간다.
    private void ReleaseAllInmates()
    {
        // Inmates는 JailZone 내부 HashSet의 뷰라, ReleaseInmate로 수정하며 순회하면 열거 예외가 난다 — 스냅샷 후 처리
        m_releaseBuffer.Clear();
        foreach (NpcController inmate in m_jailZone.Inmates)
        {
            if (inmate != null)
                m_releaseBuffer.Add(inmate);
        }

        for (int i = 0; i < m_releaseBuffer.Count; i++)
            ReleaseInmate(m_releaseBuffer[i]);

        Debug.Log($"[돌발이벤트] 범인 탈출 — 수감자 {m_releaseBuffer.Count}명 방출");
    }

    private void ReleaseInmate(NpcController inmate)
    {
        m_jailZone.ReleaseInmate(inmate);

        // 재검거의 핵심 — 판정 완료 표식을 지워야 인계존이 다시 판정한다 (#230)
        inmate.ClearDelivered();

        // 진범만 할당량·수배 후처리를 되돌린다. 경범죄(난동꾼)는 할당량·수배 대상이 아니므로 건드리지 않는다
        // (난동꾼은 CitizenIdentity.IsCriminal 대조를 타지 않는다 — MisdemeanorOffender).
        // 신원은 서버 전용 값이라 서버(또는 오프라인)에서만 도는 이 경로에서 안전하게 읽는다.
        CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
        if (identity != null && identity.IsCriminal)
        {
            if (Round != null)
                Round.ReportCriminalEscaped();
            if (WantedList != null)
                WantedList.ReinstateByNpcId(inmate.NetworkObjectId);
        }

        // 유치장 내부는 시민 통행이 금지된 NavMesh 영역(Jail)이라, 방출만 하면 나갈 경로가 없어 창살 안에
        // 고착된다 (#415) — 문 밖 출구 지점으로 내보내고 Jail 통행을 회수한 뒤 도주시킨다.
        inmate.ServerExitJail(m_jailZone.ExitPoint);

        // 유치장을 뛰쳐나와 도주한다 — 침입자를 위협으로 삼아 반대로 달아난 뒤 배회로 섞여 든다.
        // 근처에 플레이어가 없으면 도주 상태가 곧 배회로 복귀한다(NpcFleeState).
        inmate.StartFlee(m_intruder != null ? m_intruder.transform : null);

        // 방출된 난동꾼은 조용한 시민으로 남지 않는다 — 도주가 가라앉으면 원래 소란 행동을 재개한다
        // (팀 확정 2026-07-23). 침입자 등 소란 기록이 없는 개체는 무동작으로 기존대로 배회 잔류.
        MisdemeanorLoiterer.BeginRiot(inmate);
    }

    // 추적만 끊는다 — 침입자는 씬에 남는다. 검거되어 신병이 유치장으로 넘어간 경우처럼
    // "이벤트의 일은 끝났지만 NPC는 계속 살아 있어야 하는" 종료 경로에서 쓴다.
    private void StopTracking()
    {
        if (m_intruder == null)
            return;

        // 이미 해제됐더라도 -=는 중복 호출이 안전하다(미구독 시 무동작)
        m_intruder.OnIntrudeUnlockStarted -= HandleUnlockStarted;
        m_intruder.OnIntrudeFinished -= HandleIntrudeFinished;
        m_intruder.OnStateChanged -= HandleStateChanged;

        m_intruder = null;
        m_pendingStart = false;
        m_hasStarted = false;
        m_releaseQueued = false;
    }

    // 이벤트가 손을 떼고 침입자를 도심에 남긴다 — 뒷일(인계 판정·라운드 종료 정리)은
    // MisdemeanorLoiterer가 물려받는다 (SpawnedNpcEvent.ReleaseToCity와 동일 설계). (#310)
    private void ReleaseToCity()
    {
        NpcController intruder = m_intruder;
        StopTracking();
        MisdemeanorLoiterer.Attach(intruder, DisplayName);
    }

    // 침입자를 씬에서 치운다 — 이탈·불발·방치·라운드 종료 등 신병을 넘길 데가 없는 종료 경로.
    private void Despawn(bool playVfx = true)
    {
        if (m_intruder == null)
            return;

        NpcController intruder = m_intruder;
        StopTracking();

        // 연행 중인 채로 정리되면(라운드 종료 등) 연행 참조가 파괴된 NPC를 가리킨 채 남아 그 플레이어가
        // 영영 연행 중이 된다 — 파괴 전에 놓게 한다.
        // 줄다리기로 여러 명이 걸려 있을 수 있다 — 전원에게서 이 대상의 줄만 뺀다 (#390).
        foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(intruder))
            escorter.ReleaseDrag(intruder);

        SuddenEventUtil.DespawnOrDestroy(intruder.gameObject, playVfx);
    }
}
