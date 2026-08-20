using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 플레이어 납치 — <b>혼자 다니는 현장 플레이어</b>를 납치범 NPC 2명이 쫓아가 붙잡고 도시 외곽까지 끌고 간다. (GDD 6-4, #371)
///
/// 오검거 페널티(#276~#279)의 호송 파이프라인을 그대로 쓴다 — 추격(<see cref="NpcDutyAgent.StartPenaltyChase"/>) →
/// 포획 통보 → 수렴 → <see cref="CarryEscortSequence"/>. 다른 점은 <b>트리거·목적지·결말</b> 셋이다:
/// 트리거는 "혼자 있음"이고, 목적지는 광장이 아니라 외곽이며, 결말은 매달기가 아니라 <b>처형</b>이다.
///
/// <b>결말은 3단계다</b> (팀 확정 2026-08-05):
///  1. <b>린치</b> — 외곽에 도착하면 끌기를 끊어 피해자를 세우고(무력화는 유지 — 서 있되 아무것도 못 한다)
///     납치범을 저항형으로 돌려 구타한다. 저항 상태(<see cref="NpcResistState"/>)를 그대로 쓴다.
///  2. <b>처형</b> — HP가 0이 되면 기능 정지로 확정한다(<see cref="PlayerIncapacitation.ServerKillByAbduction"/>).
///  3. <b>반출</b> — 시체를 끌고 도시 바깥으로 걸어 나가고, 맵 밖에서 납치범도 함께 사라진다.
///     이 구간만 NavMesh를 벗어난다 — 그 이유는 <c>DisposeBodyAsync</c>에 적어 뒀다.
/// 반출이 끝난 뒤 그 플레이어가 라운드 남은 시간에 무엇을 하는지는 이 이벤트의 몫이 아니다(별도 이슈).
///
/// <b>구조 창은 HP 0 이전까지다</b> — 린치 중에 납치범을 전부 떼어내면 피해자는 깎인 HP로 그 자리에서
/// 풀려난다. HP 0을 넘기면 되돌릴 길이 없다: 반출은 결말의 연출이지 판정이 아니다. 이 선이 곧
/// "혼자 다니면 죽는다"의 값이다 — 되돌릴 수 있는 구간을 반출까지 늘리면 외곽까지 달려갈 이유가 사라진다.
///
/// <b>구조는 호송 중에도 된다</b> — 이것이 오검거와 정반대다. 오검거는 포획이 확정되면 격퇴가 무시되지만
/// (<see cref="NpcDutyAgent.ApplyChaseRepel"/>이 수렴 중을 걸러낸다 — "유예 창은 잡히기 전까지다", #278),
/// 납치는 동료가 끌려가는 것을 보고 달려가 <b>때리거나 무력화해</b> 떼어내는 것이 이 이벤트의 협동 지점이다.
/// 그래서 <see cref="ServerRepelAbductor"/>라는 자기 경로를 갖는다. 오검거 쪽 규칙은 건드리지 않는다.
///
/// 그 경로는 <see cref="NpcHealth.OnDamaged"/>·<see cref="NpcStun.OnStunned"/> <b>두 구독</b>으로
/// 연결돼 있다 (#554) — 진압봉도 테이저도 이 이벤트를 알 필요가 없고, 데미지나 무력화를 넣는 다른 수단이
/// 생겨도 배선 없이 함께 동작한다. 타격이 성립하려면 게이트도 열려야 하는데(납치범은 페널티군이라
/// 기본값이 '타격 불가'), 그 예외는 <see cref="NpcStateRules.CanBeDamaged"/>가 쥔다 —
/// <b>무력화는 그 게이트를 타지 않는다</b>(스턴은 #292로 전 상태에 걸린다).
///
/// <b>표적은 고정이다</b> — 오검거 추격은 표적이 범위를 벗어나면 범위 안의 다른 플레이어로 갈아타지만
/// (잡히는 사람이 독박, #276), 납치가 그러면 "혼자 있는 사람을 노린다"는 이 이벤트의 유일한 규칙이 깨진다:
/// 도망친 표적 대신 동료와 붙어 있던 사람을 잡아 곧바로 구조되는 결말이 난다. 그래서
/// <see cref="NpcDutyAgent.StartPenaltyChase"/>에 납치 임무 표식을 켜고, 범위를 벗어나도 같은 표적을
/// 계속 쫓게 한다. 놓쳤을 때의 결말은 아래 <see cref="m_maxChaseSeconds"/>가 낸다 — 개별 납치범이
/// 스스로 빠지면 남은 하나가 혼자 끌고 가 2인 호송이 무너진다. 오검거 쪽 재타겟은 그대로 둔다.
///
/// <b>포획 전에 다른 사유로 죽으면 즉시 무산한다</b> (#679) — 추격 중 죽음은 납치범이 아직 손대지
/// 못한 시점이라 언제나 외부 사유다. <see cref="HandleVictimCauseChanged"/>가 60초를 기다리지 않고 해산시킨다.
///
/// 같은 표식이 <b>앵그리 마크(#280)를 끈다</b> — 오검거 추격대를 알아보게 하는 머리 위 표시인데,
/// 납치범에게 뜨면 시민과 구분되지 않는다는 전제가 표시 하나로 깨진다.
///
/// <b>발생 방식은 돌발 이벤트다</b> — 스케줄러가 빈도를 통제하면서도 <see cref="CanTrigger"/>가 "혼자 있는
/// 플레이어가 있는가"(<see cref="LonePlayerWatch"/>가 잰다)를 보므로 "혼자 다니면 표적이 된다"는 규칙성이 남는다. 뭉쳐 있으면 발생 후보에서 빠진다.
/// 상시 위험으로 두지 않은 이유는 3인 구성이다 — 현장 둘이 늘 붙어 다녀야 하면 수사 효율이 반토막 난다.
///
/// 스폰·판정·호송은 서버(또는 오프라인)에서만 — 스폰물은 NetworkObject로 복제된다. (#56)
///
/// <b>파일이 둘로 갈려 있다</b> — 이 파일은 발동 조건·스폰·수명(ISuddenEvent 골격)을 들고,
/// 포획 이후(접수·호송·방치·구조)는 AbductionEvent.Carry.cs에 있다.
/// 오검거(WrongfulArrestPenalty + .Carry)와 같은 가름이다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public partial class AbductionEvent : MonoBehaviour, ISuddenEvent
{
    [Header("표시")]
    [SerializeField] private string m_displayName = "납치";

    [Header("납치범")]
    [Tooltip("납치범으로 스폰할 NPC 프리팹 — 시민과 같은 프리팹을 쓰면 구분되지 않는다")]
    [SerializeField] private NpcController m_abductorPrefab;

    [Tooltip("한 번에 몇 명이 달려드는가. 2명이면 양옆에서 끌고 가는 대형이 된다")]
    [Min(1)]
    [SerializeField] private int m_abductorCount = 2;

    // 공용 스폰 지점에서 좌우로 벌리는 간격(m) — 겹쳐 세우면 회피가 서로를 밀어낸다
    private const float k_spawnSlotSpacing = 1.2f;

    [Tooltip("표적에서 이 거리(m) 밖에 스폰한다 — 눈앞 팝인 방지. 2명이 이 한 지점에서 함께 나온다")]
    [SerializeField] private float m_spawnDistanceMin = 10f;

    [SerializeField] private float m_spawnDistanceMax = 18f;

    [SerializeField] private float m_navSampleMaxDistance = 4f;

    [SerializeField] private int m_maxSpawnAttempts = 8;

    [Header("경범죄 수익 (검거 시)")]
    [Tooltip("납치범을 검거하면 지급될 보상 범위 — 스폰 시점에 확정한다(재검거 리롤 방지, #395)")]
    [SerializeField] private int m_rewardMin = 500;

    [SerializeField] private int m_rewardMax = 4000;

    [Header("혼자 판정")]
    [Tooltip("표적 선정 — 반경 안에 동료가 없는 상태가 일정 시간 이어진 현장 플레이어를 고른다")]
    [SerializeField] private LonePlayerWatch m_loneWatch = new LonePlayerWatch();

    [Header("외곽 방치 지점")]
    [Tooltip("끌고 갈 목적지 후보. 붙잡힌 자리에서 가장 가까운 지점을 고른다 — NavMesh 위에 둘 것. 비우면 발동하지 않는다")]
    [SerializeField] private Transform[] m_outskirtPoints;

    [Header("호송")]
    [SerializeField] private float m_convergeArriveDistance = 2.5f;
    [SerializeField] private float m_convergeTimeoutSeconds = 20f;
    [SerializeField] private float m_carrierGap = 1.1f;
    [SerializeField] private float m_arriveDistance = 2f;
    [SerializeField] private float m_travelTimeoutSeconds = 90f;

    [Header("외곽 린치 · 시체 반출")]
    [Tooltip("도착 후 구타로 HP를 소진시키지 못해도 이 시간(초)에 강제로 끝낸다 — 교착 안전망이지 연출 값이 아니다")]
    [Min(1f)]
    [SerializeField] private float m_lynchTimeoutSeconds = 30f;

    [Tooltip("시체를 끌고 도시 바깥으로 걸어 나가는 거리(m) — 이 거리를 지나면 시체와 납치범이 함께 사라진다")]
    [Min(1f)]
    [SerializeField] private float m_disposalDistance = 25f;

    [Tooltip("반출 이동 속도(m/s) — NavMesh 밖이라 에이전트가 아니라 이 값으로 직접 민다")]
    [Min(0.1f)]
    [SerializeField] private float m_disposalSpeed = 3.5f;

    [Header("수명")]
    [Tooltip("이 시간(초) 안에 붙잡지 못하면 납치범이 포기한다 — 잔류 시민으로 남아 언제든 검거 가능")]
    [SerializeField] private float m_maxChaseSeconds = 60f;

    // 이번 납치에 동원된 납치범들. 서버(또는 오프라인) 전용.
    private readonly List<NpcController> m_abductors = new List<NpcController>();

    private bool m_active;
    private Transform m_chaseTarget;   // 포획 전 추격 표적 — 다른 사유로 죽으면 추격을 무산한다 (#679)
    private Transform m_carryTarget;   // 포획해 끌고 가는 중인 플레이어 — 중복 접수 방지
    private float m_chaseDeadline;

    // 시체 반출(DisposeBodyAsync)에 들어갔다 — 이 구간에는 격퇴가 통하지 않는다 (#554).
    // 결말이 이미 확정된 뒤이고(구조 창은 HP 0 이전까지다), 납치범은 프리즈 + 에이전트 off 상태라
    // 임무 해제가 얹히면 배회 복귀 상태의 Enter가 꺼진 에이전트를 만진다.
    private bool m_disposing;

    // 외곽 린치(LynchAsync)에 들어갔다 — 이 구간의 사인 변경은 마지막 가해자로 갈린다
    // (납치범 주먹이면 결말, 외부 사인이면 물러난다 — #554, HandleVictimCauseChanged 참고).
    private bool m_lynching;

    // 린치 상한 폴백으로 <b>우리가</b> 처형을 집행하는 중 — 무력화 감시가 이것을 외부 사인으로 오인하지 않게. (#554)
    private bool m_executing;

    // 피해자를 마지막으로 때린 자 — 린치 결말을 가르는 근거다 (#554, HandleVictimCauseChanged 참고).
    // 납치범 주먹이면 처형·반출로 끝나고, 폭발 같은 외부 사인이면 몸을 남기고 물러난다.
    private PlayerHealth m_victimHealth;
    private GameObject m_lastVictimAttacker;

    public string DisplayName => m_displayName;

    public bool IsActive => m_active;

    // <b>스폰 순간에 알린다</b> (팀 확정 2026-08-13) — 기본값(true)이지만 한 번 뒤집었다 되돌린 자리라 명시해 둔다.
    // 원래는 조용히 시작하고 포획 순간에야 알렸다: 납치범이 시민과 구분되지 않는 것이 이 이벤트의 재미이고
    // "지금 누가 노려지고 있다"를 공짜로 흘리지 않으려는 것이었다. 그 대가가 컸다 — 끌려가기 시작한 뒤에야
    // 알리면 동료가 달려갈 거리는 이미 벌어져 있고, 구조라는 협동 지점이 사실상 닫힌다.
    // 알림은 표적이 누구인지는 말하지 않으므로, 새는 것은 "어딘가에서 시작됐다"까지다.
    public bool AnnounceOnBegin => true;

    public string NoticeKey => "Hud.Event.Notice.Abduction";

    // 이벤트 프레임워크는 서버에서만 돌지만, 아래 Update는 스스로 도므로 직접 게이트한다 (JailIntake와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        m_loneWatch.ResolveSceneRefs();

        // 끌고 가던 몸이 <b>다른 사유로</b> 쓰러지는 것을 지켜본다 (#554) — 폭탄 사망이 그것이다.
        // 정적 이벤트라 플레이어 인스턴스가 새로 스폰돼도 배선이 끊기지 않는다(서버에서만 발행된다).
        PlayerIncapacitation.OnAnyIncapacitatedChanged += HandleVictimCauseChanged;

        if (m_outskirtPoints == null || m_outskirtPoints.Length == 0)
            Debug.LogWarning("AbductionEvent: 외곽 방치 지점이 배선되지 않아 발동하지 않는다", this);
    }

    // 이벤트가 사라질 때 납치범에 걸어 둔 구독을 남기지 않는다. 보통은 씬 언로드로 NPC도 함께
    // 파괴되지만, 해제 경로가 ReleaseAbductor 하나뿐이면 그 가정이 깨지는 구성(이벤트만 비활성화 등)에서
    // 파괴된 대상을 부르게 된다. <b>EndPenaltyDuty까지 부르지는 않는다</b> — 파괴 중인 NPC의
    // FSM·NavMeshAgent를 건드리게 되고, 어차피 함께 사라지는 마당에 배회로 돌려보낼 이유도 없다.
    private void OnDestroy()
    {
        PlayerIncapacitation.OnAnyIncapacitatedChanged -= HandleVictimCauseChanged;
        UntrackVictimDamage();

        for (int i = 0; i < m_abductors.Count; i++)
        {
            NpcController abductor = m_abductors[i];
            if (abductor == null)
                continue;

            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;
        }

        m_abductors.Clear();
    }

    // 혼자 판정은 <b>이벤트가 활성이 아닐 때도</b> 계속 재야 한다 — 그 이유는 LonePlayerWatch에 적어 뒀다.
    // 여기서 정하는 것은 서버 권한과 "지금 재야 하는가"뿐이다.
    private void Update()
    {
        if (!HasServerAuthority)
            return;

        // 진행 중에는 새 표적을 재지 않는다 — 끝난 뒤 다시 처음부터 센다(연속 납치 방지)
        if (m_active)
            return;

        m_loneWatch.Tick(Time.deltaTime);
    }

    public bool CanTrigger()
    {
        if (m_abductorPrefab == null || m_outskirtPoints == null || m_outskirtPoints.Length == 0)
            return false;

        return m_loneWatch.FindTarget() != null;
    }

    public void ServerBegin()
    {
        Transform target = m_loneWatch.FindTarget();
        if (target == null)
            return; // 발생 직전에 동료가 합류했다 — 이번엔 건너뛴다

        // 지점은 <b>한 번만</b> 찾고 전원이 거기서 나온다 — 각자 찾게 하면 표적을 사이에 두고 반대편에
        // 떨어져 나와 따로 접근하고, 수렴 대기(끌기 시작 전 모이는 시간)가 그만큼 길어진다. 2인조가
        // 한쪽에서 같이 걸어오는 그림이기도 하다.
        if (!TryFindGroupSpawnPosition(target, out Vector3 groupPosition))
        {
            Debug.LogWarning("AbductionEvent: 스폰 지점을 찾지 못해 발동 취소", this);
            return;
        }

        m_abductors.Clear();
        for (int i = 0; i < m_abductorCount; i++)
        {
            NpcController abductor = SpawnAbductor(groupPosition, target.position, i);
            if (abductor != null)
                m_abductors.Add(abductor);
        }

        if (m_abductors.Count == 0)
        {
            Debug.LogWarning("AbductionEvent: 납치범을 한 명도 스폰하지 못해 발동 취소", this);
            return;
        }

        // 추격 시작 — 포획 통보를 받아 호송으로 넘어간다
        for (int i = 0; i < m_abductors.Count; i++)
        {
            m_abductors[i].Penalty.OnPenaltyCaught += HandleAbductionCaught;
            m_abductors[i].Health.OnDamaged += HandleAbductorDamaged;
            m_abductors[i].Stun.OnStunned += HandleAbductorStunned;
            m_abductors[i].Penalty.StartPenaltyChase(target, NpcDutyKind.Abduction);
        }

        if (m_abductors.Count < m_abductorCount)
        {
            Debug.LogWarning(
                $"AbductionEvent: 납치범 {m_abductorCount}명 중 {m_abductors.Count}명만 스폰됐다 — 스폰 지점을 못 찾았다", this);
        }

        m_active = true;
        m_chaseTarget = target;
        m_chaseDeadline = Time.time + m_maxChaseSeconds;
        m_loneWatch.Reset(); // 이번 판정은 소비했다 — 끝난 뒤 처음부터 다시 센다

        Debug.Log($"[납치] 발동 — 표적 {target.name}, 납치범 {m_abductors.Count}명");
    }

    // 2인조가 함께 나올 지점 — 난동꾼(SpawnedNpcEventBase)과 같은 경로다.
    // 1순위는 보이지 않는 지점이다. 다만 뻥 뚫린 거리에서는 링(min~max) 안에 가려주는 지형이 없어
    // 전 시도가 탈락하기 쉬운데, 여기서 포기하면 발동 자체가 조용히 불발된다. 대형이 깨지는 것보다
    // 팝인이 보이는 편이 낫다 — 거리는 어차피 지켜지므로 눈앞에 솟지는 않는다.
    private bool TryFindGroupSpawnPosition(Transform target, out Vector3 position)
    {
        int areaMask = SuddenEventUtil.SpawnAreaMask(m_abductorPrefab);

        return SuddenEventUtil.TryFindSpawnPositionNear(
                   target.position, m_spawnDistanceMin, m_spawnDistanceMax,
                   m_navSampleMaxDistance, m_maxSpawnAttempts, areaMask,
                   out position, hiddenFromPlayers: true)
               || SuddenEventUtil.TryFindSpawnPositionNear(
                   target.position, m_spawnDistanceMin, m_spawnDistanceMax,
                   m_navSampleMaxDistance, m_maxSpawnAttempts, areaMask,
                   out position, hiddenFromPlayers: false);
    }

    // 공용 지점에서 index번 납치범을 스폰한다 — 표적을 바라보는 방향 기준으로 좌우로 조금씩 벌려 세운다.
    // 완전히 같은 좌표에 겹쳐 놓으면 NavMeshAgent 회피가 서로를 밀어내며 첫 몇 초를 낭비한다.
    private NpcController SpawnAbductor(Vector3 groupPosition, Vector3 targetPosition, int index)
    {
        Vector3 toTarget = targetPosition - groupPosition;
        toTarget.y = 0f;
        Vector3 forward = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);

        // 인원 수 기준으로 가운데 정렬 — 2명이면 ±(간격/2)
        float slot = (index - (m_abductorCount - 1) * 0.5f) * k_spawnSlotSpacing;
        Vector3 spawnPosition = groupPosition + side * slot;

        // 벌린 자리가 NavMesh 밖(인도 끝·벽)일 수 있다 — 못 붙이면 공용 지점 그대로 쓰고 회피에 맡긴다
        if (NavMesh.SamplePosition(spawnPosition, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
            spawnPosition = hit.position;
        else
            spawnPosition = groupPosition;

        NpcController abductor = Instantiate(
            m_abductorPrefab, spawnPosition, Quaternion.LookRotation(forward, Vector3.up));

        // 경범죄 표식 — 검거하면 진범 대조 대신 경범죄로 판정되고 이 보상이 실린다 (#106과 같은 관례).
        // 이 한 줄이 없으면 납치를 막은 플레이어가 오검거 페널티를 먹는다 — 대응에 성공한 쪽이 손해 본다.
        abductor.gameObject.AddComponent<MisdemeanorOffender>().Reward =
            BountyRoll.Roll(m_rewardMin, m_rewardMax);

        if (SuddenEventUtil.IsNetworkSessionActive)
            abductor.GetComponent<NetworkObject>().Spawn();

        // 신원 배정은 부르지 않는다 — CitizenIdentity가 Start에서 스스로 요청한다 (#505).
        // 납치범도 시민과 똑같이 스캔되지만 인명부에는 실리지 않는다(런타임 스폰 NPC 공통 규칙).

        return abductor;
    }

    public void ServerTick()
    {
        if (!m_active)
            return;

        PruneDead(m_abductors);

        // 납치범이 전멸(검거·격퇴)했다 — 호송 중이면 시퀀스가 스스로 끝내므로 여기선 추격 단계만 본다
        if (m_abductors.Count == 0 && m_carryTarget == null)
        {
            Debug.Log("[납치] 종료 — 납치범이 남지 않았다");
            Finish();
            return;
        }

        // 제 시간에 붙잡지 못했다 — 포기하고 잔류 시민으로 남는다(언제든 검거 가능, #310과 같은 처리)
        if (m_carryTarget == null && Time.time >= m_chaseDeadline)
        {
            Debug.Log("[납치] 종료 — 제 시간에 붙잡지 못해 포기");
            ReleaseAllAbductors();
            Finish();
        }
    }

    public void ServerReset()
    {
        // 라운드 종료 등 강제 정리 — 끌려가던 플레이어를 풀어 주고 납치범을 놓는다.
        // 참조를 <b>먼저</b> 비운다: 아래 Recover가 무력화 감시(HandleVictimCauseChanged)를 울리는데,
        // 그때 m_carryTarget이 남아 있으면 스스로 푼 것을 외부 사유로 오인해 중단 로그가 뜬다 (#554).
        Transform released = m_carryTarget;
        m_carryTarget = null;

        if (released != null)
        {
            PlayerIncapacitation incap = released.GetComponent<PlayerIncapacitation>();
            if (incap != null
                && (incap.Cause == IncapacitationCause.Abducted || incap.Cause == IncapacitationCause.Lynched))
                incap.Recover();
        }

        ReleaseAllAbductors();
        Finish();
    }

    // 임무 해제 — 구독을 풀고 배회 시민으로 돌려보낸다. 스폰물을 지우지는 않는다:
    // 잔류 시민으로 남아 언제든 검거·인계할 수 있어야 한다 ("아까 놓친 그 놈", #310).
    //
    // 뒷정리(라운드 종료)는 <see cref="MisdemeanorLoiterer"/>가 물려받는다 — 난동꾼·침입자의
    // ReleaseToCity와 같은 설계다. 이 한 줄이 빠지면 스폰물이 라운드를 넘겨 살아남는다:
    // 스폰이 Spawn()(destroyWithScene 기본 false)이라 씬 언로드로도 지워지지 않고, 잔류 정리를
    // 물려받은 데가 없어 라운드 종료 정리도 타지 않는다. 검거돼 유치장에 앉은 납치범이 다음
    // 라운드까지 그 자리에 남아 있던 것이 이 누락이었다.
    //
    // 검거되는 납치범은 반드시 여기를 지난다 — 밧줄은 무력화된 대상만 묶는데(NpcStateRules.CanRopeBind)
    // 무력화에 필요한 타격이 곧 격퇴(HandleAbductorDamaged)라 임무 해제가 먼저 일어난다.
    // 그래서 수감 경로를 따로 잡지 않고 여기 한 곳에서 넘긴다.
    private void ReleaseAbductor(NpcController abductor)
    {
        if (abductor != null)
        {
            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;
            abductor.Penalty.EndPenaltyDuty();
            MisdemeanorLoiterer.Attach(abductor, m_displayName);
        }

        m_abductors.Remove(abductor);
    }

    private void ReleaseAllAbductors()
    {
        for (int i = m_abductors.Count - 1; i >= 0; i--)
            ReleaseAbductor(m_abductors[i]);
    }

    /// <summary>
    /// 반출 완료 — 납치범을 씬에서 치운다. <see cref="ReleaseAbductor"/>와 <b>갈리는 경로</b>다:
    /// 그쪽은 도심에 잔류 시민으로 남겨 언제든 검거할 수 있게 하지만(#310), 시체를 끌고 맵 밖까지
    /// 나간 놈은 잔류하지 않는다(팀 확정 2026-08-05). 그래서 잔류 정리를 물려줄
    /// <see cref="MisdemeanorLoiterer"/>도 붙이지 않는다 — 여기서 바로 사라지기 때문이다.
    /// 반출이 도중에 실패해도 부른다: 어느 경로로 끝나든 납치범이 씬에 남지 않게 하는 것이 이 함수의 계약이다.
    /// </summary>
    private void DisposeAbductors()
    {
        for (int i = m_abductors.Count - 1; i >= 0; i--)
        {
            NpcController abductor = m_abductors[i];
            if (abductor == null)
                continue;

            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;

            // 끌고 있던 플레이어가 파괴된 참조를 쥐지 않게 먼저 놓게 한다 (다른 이벤트의 Despawn과 동일)
            foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(abductor))
                escorter.ReleaseDrag(abductor);

            SuddenEventUtil.DespawnOrDestroy(abductor.gameObject, playVfx: false);
        }

        m_abductors.Clear();
    }

    private void Finish()
    {
        m_active = false;
        m_chaseTarget = null;
        m_disposing = false;
        m_lynching = false;
        m_executing = false;
        UntrackVictimDamage();
        m_loneWatch.Reset(); // 다음 프레임부터 혼자 판정을 처음부터 다시 센다
    }

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);
}
