using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 밧줄 연결 상태 — <b>누구를 묶고 있고, 그 연결을 매 프레임 어떻게 유지하는가</b>. (#269/#369/#390/#398)
/// 밧줄 <b>1개당 NPC 1명</b>이라 연결은 목록이고, 동시 인원의 상한은 소지한 밧줄 개수다.
///
/// 요청·검증·채널링은 <see cref="PlayerEscortCommands"/>가 갖는다 — 구동 주체가 다르기 때문이다:
/// 저쪽은 플레이어 입력이 올 때만 돌고(좌클릭·E), 이쪽은 서버에서 매 프레임 돈다. 의존은
/// <c>Commands → Escorter</c> 한 방향뿐이고, 목록의 소유자는 이 컴포넌트 하나다.
///
/// 장력 계산 자체는 끌리는 <see cref="NpcController"/>가, 끄는 쪽이 지는 대가(무게·목줄 제한, #398)는
/// <see cref="RopeDragLoad"/>가 갖는다 — 여기 있는 것은 "누구를 묶고 있나"의 참조 관리와
/// 커스터디 이탈·거리 끊김 감지다. 목줄 여부(<see cref="IsLeashedTo"/>)와 끊김 거리는
/// 끊김 판정과 이동 제한이 같은 기준을 봐야 해서 이 컴포넌트가 단일 진실로 갖는다.
///
/// 오너 판정 피드백(<see cref="OwnerFeedback"/>, #91)만 쓴다 — 줄 끊김·놓기를 오너 화면에 알려야 한다.
/// 채널링은 하지 않으므로 <see cref="ChannelGauge"/>는 붙이지 않는다.
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerEscorter : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    // internal인 이유 — 같은 클래스의 다른 인스턴스(holder)에게 알리는 자리가 있다(ServerHandleJailExit).
    // 상속 시절에는 protected 접근으로 됐던 것이 합성 후에는 이 접근자를 거친다.
    internal OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    [Header("밧줄 끌기")]
    // 장력 튜닝 값(길이·스무딩·흔들림·간격)은 NpcRopeDragConfig에 있다 — 장력 계산과 같은 자리.
    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    /// <summary>
    /// 줄이 끊기는 거리(m) — 끊김 판정(이 컴포넌트)과 목줄 반경(<see cref="RopeDragLoad"/>)이
    /// 같은 값을 봐야 해서 여기 하나만 둔다. 따로 두면 "끊기는데 안 막히는" 구간이 생긴다.
    /// </summary>
    internal float RopeBreakDistance => m_ropeBreakDistance;

    // 내 밧줄에 묶여 있는 NPC들 — 서버(또는 오프라인) 진실. 끌기를 멈춰도(E) 남는다.
    private readonly List<NpcController> m_tethered = new List<NpcController>();

    // 위 목록의 클라 사본(서버만 쓴다). 표시(RopeDragView)가 선의 양 끝점을 알아야 하고,
    // 오너 조기검증(Rope.CanTarget·E 놓기 대상)도 "내가 이걸 묶었나"를 물어야 한다.
    // 항목마다 '끌고 있는가'를 싣는 이유: 줄다리기로 한 NPC에 여러 명이 걸리면
    // NpcRopeDrag.IsRoped("누구든 끌고 있다")로는 내가 놓았는지를 알 수 없다.
    // ⚠ late-join 클라는 OnListChanged를 못 받는다 — 읽는 쪽이 현재 목록을 직접 훑을 것 (WantedListManager와 같은 주의).
    private readonly NetworkList<RopeTether> m_tetheredSynced = new NetworkList<RopeTether>();

    /// <summary>지금 밧줄에 묶여 있는 인원 수. 전 피어에서 유효.</summary>
    public int TetheredCount => IsSpawned && !IsServer ? m_tetheredSynced.Count : m_tethered.Count;

    private const int k_leashDraggerCount = 2;

    // 끌기의 대가(무게·목줄) — 매 프레임 이 컴포넌트가 정리를 끝낸 뒤 돌린다. (#398)
    private RopeDragLoad m_load;

    private RopeDragLoad Load
    {
        get
        {
            if (m_load == null)
                m_load = GetComponent<RopeDragLoad>();
            return m_load;
        }
    }

    // 수갑 자원 게이트·소모용 로드아웃 (#229). 테스트 구성 등 없을 수 있어 null 허용.
    private PlayerLoadout m_loadout;

    private PlayerLoadout Loadout
    {
        get
        {
            if (m_loadout == null)
                m_loadout = GetComponent<PlayerLoadout>();
            return m_loadout;
        }
    }

    // 기능 정지된 동료를 끄는 중인가 (#365) — 운반도 밧줄 한 개를 쓰므로 용량 계산(RopesInUse)에 들어간다.
    // '한 번에 1명'이 폐기된 뒤(#390) 운반과 NPC 끌기는 배타가 아니라 같은 자원을 나눠 쓰는 관계다.
    // 없는 구성(테스트 등)이면 false.
    //
    // 무게 계산(<see cref="RopeDragLoad.ServerTickWeight"/>, #546)도 같은 값을 읽는다 — 줄 한 개를
    // 쓰는 것과 그만큼 무거워지는 것은 같은 사실의 양면이라, 두 곳이 각자 조회하면 갈라질 수 있다.
    private PlayerCarrier m_carrier;

    /// <summary>이 플레이어의 운반 허브 — <see cref="RopeDragLoad"/>가 무게·목줄 계산에 빌려 읽는다.
    /// 상태 소유는 여전히 <see cref="PlayerCarrier"/>다(이 클래스는 조회 경로만 겸한다).</summary>
    internal PlayerCarrier CarriedPlayer
    {
        get
        {
            if (m_carrier == null)
                m_carrier = GetComponent<PlayerCarrier>();
            return m_carrier;
        }
    }

    internal bool IsCarryingPlayer => CarriedPlayer != null && CarriedPlayer.IsCarrying;

    /// <summary>동시에 묶을 수 있는 상한 — 로드아웃이 없으면(테스트 구성) 무제한.</summary>
    internal int RopeCapacity => Loadout != null ? Loadout.RopeCount : int.MaxValue;

    // 지금 쓰고 있는 줄 수 — 묶어 둔 NPC + 기능 정지 동료 운반 1명(#365). 동료도 같은 밧줄로 끌기 때문에
    // NPC와 같은 자원 풀을 나눠 쓴다: 밧줄 2개면 NPC 1명을 끌면서 동료 1명을 옮길 수 있고, 1개면 둘 중 하나다.
    private int RopesInUse => TetheredCount + (IsCarryingPlayer ? 1 : 0);

    /// <summary>소지한 밧줄을 전부 쓰고 있는가 — 새 대상을 묶는(또는 동료를 드는) 것을 막는 자원 게이트.</summary>
    public bool IsAtRopeCapacity => RopesInUse >= RopeCapacity;

    /// <summary>
    /// 서버 진실 목록 — <see cref="PlayerEscortCommands"/>의 인계 순회 전용. 서버(또는 오프라인)에서만 유효.
    /// 순회 중 목록이 바뀌므로(판정 성공 → Jailed → 정리) 읽는 쪽이 복사해서 돌 것.
    /// </summary>
    internal IReadOnlyList<NpcController> ServerTethered => m_tethered;

    // ---- 정적 조회 (전 피어) ----

    // 살아 있는 인스턴스 목록 — 아래 조회가 인계 판정마다 씬을 뒤지지 않게 한다
    // (PlayerIncapacitation.All과 같은 패턴, #961). 집합 정의는 PlayerHealth.All 주석 참고.
    private static readonly List<PlayerEscorter> s_instances = new List<PlayerEscorter>();

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    /// <summary>
    /// 해당 NPC를 밧줄에 묶고 있는 플레이어를 찾는다 — 없으면 null. 서버(또는 오프라인)에서만 유효.
    /// 인계 판정(ArrestJudge)이 이 결과로 끌기를 물리적으로 풀기 때문에, 빼면 인계자가 "알 수 없음"이 되고
    /// 끌기가 안 풀린 채(에이전트 꺼진 채) 상태 전이가 일어나 NavMeshAgent 예외가 난다. (#269)
    /// 여러 명이 걸려 있으면 그중 하나다 — 전원이 필요하면 <see cref="FindEscortersOf"/>.
    /// </summary>
    public static PlayerEscorter FindEscorterOf(NpcController npc)
    {
        if (npc == null)
            return null;

        for (int i = 0; i < s_instances.Count; i++)
        {
            PlayerEscorter escorter = s_instances[i];
            if (escorter != null && escorter.IsTetheredTo(npc))
                return escorter;
        }

        return null;
    }

    /// <summary>
    /// 해당 NPC에 밧줄을 걸고 있는 플레이어를 <b>전부</b> 찾는다 — 아무도 없으면 빈 목록. 서버(또는 오프라인) 전용.
    /// 줄다리기로 여러 명이 한 대상에 걸릴 수 있어, 인계 판정·오검거 페널티처럼 "관여한 사람 전원"을
    /// 알아야 하는 쪽이 이걸 쓴다.
    /// </summary>
    public static List<PlayerEscorter> FindEscortersOf(NpcController npc)
    {
        var found = new List<PlayerEscorter>();
        if (npc == null)
            return found;

        for (int i = 0; i < s_instances.Count; i++)
        {
            PlayerEscorter escorter = s_instances[i];
            if (escorter != null && escorter.IsTetheredTo(npc))
                found.Add(escorter);
        }

        return found;
    }

    /// <summary>
    /// 이 대상에 걸린 밧줄을 <b>전부</b> 걷어내고 일으켜 세운다 — 다 일어난 뒤
    /// <paramref name="afterStandUp"/>을 실행한다. 서버(또는 오프라인) 전용. (#537)
    ///
    /// 수감 버튼(<see cref="JailIntakeButton"/> → <see cref="JailIntake"/>)이 쓴다: 판정 대상은 묶여
    /// 누운 채로 와 있으므로, 줄을 걷고 일어난 다음에야 그 뒤 처리(순간이동·석방)가 그림이 된다.
    ///
    /// <b>세 단계의 순서가 강제다</b> (#513): 끌기 해제 → 일어나기 예약 → 줄 빼기.
    /// 줄을 먼저 빼면 대상의 묶임 표시가 내려가 <c>ServerStandUpThen</c>이 "이미 서 있다"로 오판해
    /// 일어나기가 통째로 생략되고, 누운 몸이 그대로 미끄러진다.
    /// (묶인 적 없이 제압만으로 잡힌 대상은 <c>ServerStandUpThen</c>이 알아서 즉시 실행한다)
    /// </summary>
    public static void ReleaseAllTethersOn(NpcController npc, System.Action afterStandUp)
    {
        if (npc == null)
            return;

        List<PlayerEscorter> holders = FindEscortersOf(npc);

        for (int i = 0; i < holders.Count; i++)
            holders[i].ReleaseDrag(npc); // 끌기 해제 — 에이전트를 되살린다(순간이동이 성립하려면 필요하다)

        npc.StandUp.ServerStandUpThen(afterStandUp);

        for (int i = 0; i < holders.Count; i++)
            holders[i].RemoveTether(npc);
    }

    /// <summary>
    /// 시체에 걸린 밧줄을 전부 걷어낸다 — <b>일으켜 세우지 않는다.</b> 서버(또는 오프라인) 전용. (#571)
    ///
    /// <see cref="ReleaseAllTethersOn"/>의 시체판이고, 갈리는 것은 <c>ServerStandUpThen</c> 하나다.
    /// 그 기상 예약을 시체에 걸면 안 된다: 예약이 끝난 뒤 커스터디 전이를 거는데 <see cref="NpcState.Dead"/>
    /// 에서는 나갈 수 없어 <c>NpcStateMachine</c>이 에러만 남기고, 그 전에 죽은 몸이 일어나는 모션이 한 번 난다.
    /// (거리 끊김도 같은 이유로 시체를 따로 가른다 — <see cref="TickTetherCleanup"/>)
    ///
    /// <see cref="ReleaseDrag"/>가 관절 밧줄까지 풀어 준다(<c>NpcRopeDrag.StopRopeDrag</c>) — 시체는
    /// 에이전트도 되살아나지 않으므로, 남는 것은 그 자리에 누운 몸뿐이다.
    /// </summary>
    public static void ReleaseAllTethersOnCorpse(NpcController npc) =>
        ReleaseTethersOnCorpseExcept(npc, null);

    /// <summary>
    /// 시체에 걸린 밧줄 중 <paramref name="keeper"/>의 것만 남기고 <b>나머지를 전부 끊는다</b>.
    /// <paramref name="keeper"/>가 null이면 전부 끊는다(= <see cref="ReleaseAllTethersOnCorpse"/>).
    /// 서버(또는 오프라인) 전용. (#757)
    ///
    /// 감옥 문으로 시체를 데리고 나가는 경로가 쓴다 — 나가는 사람의 줄만 남기고 <b>감옥에 남은 참가자의
    /// 줄은 끊는다</b>. 안 끊으면 줄이 벽을 뚫고 셀까지 이어진 채로 남는데, 줄다리기 중에는
    /// <see cref="TickTetherCleanup"/>의 거리 끊김이 목줄 예외(<see cref="IsLeashedTo"/>)에 걸려
    /// <b>영영 자기 치유가 안 된다.</b>
    ///
    /// ⚠ <b>두 줄을 항상 같이 부른다.</b> <see cref="ReleaseDrag"/>는 <b>끌기</b>만 멈추고 줄은 남기며,
    /// 끌고 있지 않으면(E로 놓아둔 줄) 아예 조기 반환한다 — 실제로 끊는 것은 <see cref="RemoveTether"/>다.
    /// </summary>
    public static void ReleaseTethersOnCorpseExcept(NpcController npc, PlayerEscorter keeper)
    {
        if (npc == null)
            return;

        List<PlayerEscorter> holders = FindEscortersOf(npc);

        for (int i = 0; i < holders.Count; i++)
        {
            PlayerEscorter holder = holders[i];
            if (holder == keeper)
                continue;

            holder.ReleaseDrag(npc);
            holder.RemoveTether(npc); // 밧줄 칸을 돌려준다 — 안 빼면 매 프레임 정리가 돌 때까지 물린다

            // 남긴 사람이 있다는 것은 이 줄이 남의 사정으로 끊겼다는 뜻이다 — 왜 사라졌는지 알려야 한다.
            if (keeper != null)
                holder.Feedback?.NotifyOwner($"밧줄 끊김 — 다른 참가자가 감옥 밖으로 데리고 나갔다: {npc.name}");
        }
    }

    /// <summary>
    /// 나 말고 이 대상을 묶고 있는 사람이 있는가 — 서버(또는 오프라인) 전용. (#513)
    /// 풀기가 <b>내 줄을 빼기 전에</b> 물어야 하는 질문이다: 뺀 뒤에 <see cref="FindEscorterOf"/>로 물으면
    /// 답은 같지만, 그때는 대상의 묶임 표시가 이미 내려가 "묶여 누워 있었는가"를 알 수 없다.
    /// </summary>
    internal bool HasOtherTether(NpcController npc)
    {
        List<PlayerEscorter> holders = FindEscortersOf(npc);
        for (int i = 0; i < holders.Count; i++)
            if (holders[i] != this)
                return true;

        return false;
    }

    // ---- 연결 조회 (전 피어) ----

    /// <summary>묶인 대상을 순번으로 얻는다 — 전 피어에서 유효한 표현·검증용. 없거나 못 찾으면 null.</summary>
    public NpcController GetTetheredNpc(int index)
    {
        if (!IsSpawned || IsServer)
            return index >= 0 && index < m_tethered.Count ? m_tethered[index] : null;

        if (index < 0 || index >= m_tetheredSynced.Count)
            return null;

        // 세션이 내려가는 중에는 매니저가 이미 사라져 있다. IsSpawned만으로는 이 순간을 거를 수 없어
        // (디스폰 통지보다 매니저 소멸이 앞설 수 있다) 라운드 종료 후 씬이 바뀌는 동안
        // 표시(RopeDragView.LateUpdate)가 매 프레임 NullReferenceException을 뱉는다.
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return null;

        return manager.SpawnManager.SpawnedObjects.TryGetValue(
            m_tetheredSynced[index].NpcId, out NetworkObject npcObject)
            && npcObject.TryGetComponent(out NpcController npc)
            ? npc
            : null;
    }

    /// <summary>이 NPC가 <b>내</b> 밧줄에 묶여 있는가 — 전 피어에서 유효. 좌클릭 분기·E 놓기 대상 판정이 쓴다.</summary>
    public bool IsTetheredTo(NpcController npc) => IndexOfTether(npc) >= 0;

    /// <summary>이 NPC를 <b>내가 지금 끌고</b> 있는가 — 전 피어에서 유효.
    /// 묶여만 있는(E로 놓아둔) 대상은 <b>남이 대신 끌고 있어도</b> false다 — 그래야 줄다리기 중
    /// 내 E가 계속 '놓기'로 소비되지 않는다.</summary>
    public bool IsDraggingNpc(NpcController npc)
    {
        if (!IsTetheredTo(npc))
            return false;

        // 서버는 NPC의 앵커 목록이 단일 진실. 클라는 그게 실려 온 동기화 항목을 읽는다.
        if (!IsSpawned || IsServer)
            return npc.Rope.IsDraggedBy(transform);

        int index = IndexOfSynced(npc);
        return index >= 0 && m_tetheredSynced[index].Dragging;
    }

    /// <summary>지금 <b>끌고 있는</b> 대상이 하나라도 있는가 — 전 피어에서 유효. (#638)
    /// 겨냥 없는 E가 '손 떼기'로 소비될지를 오너가 미리 가르는 데 쓴다 — 끄는 것이 없으면
    /// E는 평소 상호작용으로 그대로 흘러가야 한다. 판정 기준은 <see cref="IsDraggingNpc"/>와 같다.</summary>
    public bool IsDraggingAny
    {
        get
        {
            if (!IsSpawned || IsServer)
            {
                for (int i = 0; i < m_tethered.Count; i++)
                    if (m_tethered[i] != null && m_tethered[i].Rope.IsDraggedBy(transform))
                        return true;
                return false;
            }

            for (int i = 0; i < m_tetheredSynced.Count; i++)
                if (m_tetheredSynced[i].Dragging)
                    return true;
            return false;
        }
    }

    // 이 NPC가 내 목록의 몇 번째인가 — 없으면 -1. 전 피어에서 유효하되 인덱스는 서버·클라가 다를 수 있다
    // (미스폰 NPC는 서버 목록에만 들어가 길이가 어긋난다).
    private int IndexOfTether(NpcController npc)
    {
        if (npc == null)
            return -1;

        return !IsSpawned || IsServer ? m_tethered.IndexOf(npc) : IndexOfSynced(npc);
    }

    // 동기화 목록에서 이 NPC의 항목 위치 — 없거나 id를 못 읽으면 -1.
    private int IndexOfSynced(NpcController npc)
    {
        if (npc == null || npc.NetworkObject == null)
            return -1;

        ulong id = npc.NetworkObject.NetworkObjectId;
        for (int i = 0; i < m_tetheredSynced.Count; i++)
            if (m_tetheredSynced[i].NpcId == id)
                return i;
        return -1;
    }

    // ---- 연결 목록 관리 (서버·오프라인 전용) ----

    /// <summary>
    /// 연결을 맺는다 — 묶기·합류·재개가 전부 여기로 온다 (<see cref="PlayerEscortCommands"/>가 검증 후 호출).
    /// 직후 StartRopeDrag로 앵커가 붙으므로 항상 '끌고 있음'으로 시작한다.
    /// </summary>
    internal void AddTether(NpcController npc)
    {
        if (npc == null)
            return;

        if (!m_tethered.Contains(npc))
        {
            m_tethered.Add(npc);
            npc.Rope.AddTether(); // 대상도 "묶여 있음"을 알아야 놓은 뒤에도 누운 자세가 유지된다 (#513)
        }

        SetTetherDragging(npc, true);
    }

    // 동기화 항목의 끌기 표시를 갱신한다 — 값이 그대로면 쓰지 않는다(매 프레임 정리가 불러도 대역폭을 먹지 않게).
    // 서버 자신은 이 표시를 읽지 않는다(NpcController.IsDraggedBy가 진실) — 순전히 클라에 알리는 용도다.
    private void SetTetherDragging(NpcController npc, bool dragging)
    {
        if (!IsSpawned || !IsServer)
            return;

        int index = IndexOfSynced(npc);
        if (index < 0)
        {
            // 스폰된 대상만 동기화 목록에 실을 수 있다 — 아니면 표시 없이 끌기만 진행된다(오프라인 테스트 등)
            if (npc.NetworkObject != null && npc.NetworkObject.IsSpawned)
            {
                m_tetheredSynced.Add(
                    new RopeTether { NpcId = npc.NetworkObject.NetworkObjectId, Dragging = dragging });
            }
            return;
        }

        RopeTether entry = m_tetheredSynced[index];
        if (entry.Dragging == dragging)
            return;

        entry.Dragging = dragging;
        m_tetheredSynced[index] = entry;
    }

    /// <summary>이 대상과의 연결을 끊는다 — 밧줄 풀기(<see cref="PlayerEscortCommands"/>) 전용. 서버(또는 오프라인).</summary>
    internal void RemoveTether(NpcController npc)
    {
        int index = m_tethered.IndexOf(npc);
        if (index >= 0)
            RemoveTetherAt(index);
    }

    // 인덱스로 지운다 — 매 프레임 정리가 역순 순회하며 부르기 때문. npc가 이미 파괴됐을 수 있다.
    private void RemoveTetherAt(int index)
    {
        NpcController npc = m_tethered[index];
        m_tethered.RemoveAt(index);

        // 대상의 묶임 표시도 한 칸 줄인다 — 파괴된 대상은 셀 필요가 없다 (#513)
        if (npc != null)
            npc.Rope.RemoveTether();

        if (!IsSpawned || !IsServer)
            return;

        // 살아 있으면 id로 정확히 지우고, 파괴돼 id를 못 읽으면 죽은 항목을 훑어 정리한다.
        int syncedIndex = IndexOfSynced(npc);
        if (syncedIndex >= 0)
        {
            m_tetheredSynced.RemoveAt(syncedIndex);
            return;
        }

        if (npc == null || npc.NetworkObject == null)
            PruneDeadSyncedEntries();
    }

    // 대상이 파괴·디스폰돼 더는 풀리지 않는 항목을 걷어낸다 — 남겨두면 원격 피어가 없는 줄을 계속 찾는다.
    private void PruneDeadSyncedEntries()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return;

        for (int i = m_tetheredSynced.Count - 1; i >= 0; i--)
            if (!manager.SpawnManager.SpawnedObjects.ContainsKey(m_tetheredSynced[i].NpcId))
                m_tetheredSynced.RemoveAt(i);
    }

    // ---- 매 프레임 연결 유지 (서버·오프라인 전용) ----

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 끌기 상태 자체가 서버 권위다 (#56/#118).
        if (IsSpawned && !IsServer)
            return;

        // 순서 강제 — 정리가 먼저 돌아야 사라진 대상이 무게 계산에 잡히지 않는다.
        TickTetherCleanup();
        Load?.ServerTickWeight();
    }

    /// 밧줄 연결 매 프레임 정리. 장력은 <see cref="NpcController"/>가 계산한다 —
    /// 여기는 커스터디 이탈·거리 끊김에 따른 참조 관리만.
    private void TickTetherCleanup()
    {
        // 지우면서 도니 역순 — 각 연결은 서로 독립이라 하나가 끊겨도 나머지는 유지된다.
        for (int i = m_tethered.Count - 1; i >= 0; i--)
        {
            NpcController npc = m_tethered[i];

            // 대상이 커스터디를 벗어나면 밧줄 연결도 끊는다 — 인계 판정(→Jailed)·방치 탈주·풀기(→Idle)·
            // 라운드 종료 파괴가 전부 여기로 수렴한다(참조가 Unity 가짜 null이 되는 파괴 경로 포함, #356).
            if (npc == null || !IsTetherableState(npc))
            {
                RemoveTetherAt(i);
                continue;
            }

            // 너무 멀어지면 줄이 끊겨 풀려나 달아난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어간 경우 (#369).
            // ReleaseDrag가 먼저인 이유: 도주(Run)가 NavMesh를 쓰는데 끌기 중엔 에이전트가 꺼져 있다.
            // 목줄이 걸린 동안(줄다리기)만 예외다 (#398) — 둘을 함께 켜 두면 같은 거리를 경계로
            // "막힘"과 "끊김"이 매 프레임 다툰다.
            if (!IsLeashedTo(npc) && IsTooFarToTether(npc))
            {
                // 시체는 일어나지도 달아나지도 않는다 (#571) — 줄만 끊고 그 자리에 남긴다.
                // 아래 두 갈래(도주 / 그 자리에 남기)는 둘 다 ServerStandUpThen을 태우는데, 그건
                // 시체에 걸면 안 된다: 기상 예약이 끝난 뒤 커스터디 전이를 거는데 Dead에서는 나갈 수
                // 없어 NpcStateMachine이 에러만 남긴다.
                if (npc.Death.IsDead)
                {
                    ReleaseDrag(npc);
                    Feedback?.NotifyOwner($"밧줄 끊김 — 시체를 놓쳤다: {npc.name}");
                    RemoveTetherAt(i);
                    continue;
                }

                // 내 줄을 빼기 <b>전에</b> 물어야 한다 — 뺀 뒤에는 대상의 묶임 표시가 이미 내려가
                // "묶여 누워 있었는가"를 알 수 없다 (#513, ServerApplyUnrope와 같은 순서).
                bool othersHold = HasOtherTether(npc);

                ReleaseDrag(npc);

                // 다른 참가자가 아직 잡고 있으면 도주시키지 않는다 — 내 줄만 끊긴 것이다.
                // 줄다리기에서 밀린 쪽이 빠지는 정상 결말이라, 여기서 도주시키면 이긴 쪽 손에서 사라진다.
                if (othersHold)
                {
                    RemoveTetherAt(i);
                    Feedback?.NotifyOwner($"밧줄 끊김 — 내 줄만 끊겼다 (다른 참가자가 계속 확보 중): {npc.name}");
                    continue;
                }

                // 마지막 줄이 끊기는 순간이 곧 일어나는 순간이다 (#513) — 여기까지는 묶인 채 누워 있었다.
                // 아래 두 예약 모두 <see cref="RemoveTetherAt"/>보다 <b>앞</b>이어야 한다: 줄을 먼저 빼면
                // 묶임 표시가 내려가 ServerStandUpThen이 "이미 서 있다"로 오판해 일어나기가 통째로 생략된다.
                //
                // 유치장 안이거나 판정이 끝난 대상은 <b>달아나지 않는다</b> (#526) — 방치 만료와 같은 기준을
                // 본다. 이 가드가 없으면 잠긴 유치장 안에 묶어 둔 수감자가 줄이 끊기는 순간 Jail 통행을 든 채
                // 도주로 전환돼 창살을 통과해 나간다(창살 콜라이더는 플레이어만 막는다).
                if (NpcStateRules.StaysPutWhenFreed(npc))
                {
                    Feedback?.NotifyOwner($"밧줄 끊김 — 달아나지 않고 그 자리에 남는다: {npc.name}");
                    npc.StandUp.ServerStandUpThen(null);
                }
                else
                {
                    // 위협은 줄이 끊긴 그 플레이어다. 예약은 일어나기가 끝난 뒤 실행되므로 그때 이
                    // 컴포넌트가 이미 사라져 있을 수 있어 transform을 지역 변수로 잡아 둔다 —
                    // 그새 파괴됐어도 NpcFleeState가 위협 없는 도주로 받아 준다(ThreatTarget null 검사).
                    Transform threat = transform;
                    Feedback?.NotifyOwner($"밧줄 끊김 — 너무 멀어져 도주: {npc.name}");
                    // 질주하던 개체(공연음란범)는 도주가 아니라 질주로 돌아간다 (#106)
                    npc.StandUp.ServerStandUpThen(() => npc.Reaction.ResumeReaction(threat));
                }

                RemoveTetherAt(i);
                continue;
            }

            // 외부 요인으로 커스터디에서 벗어났으면(넉백·페널티 등 강제 상태 전이) 끌기만 정리한다 — 줄은 유지.
            // 시체는 여기 걸리지 않는다 — 애초에 Escorted로 들어가지 않으므로 이 검사로는 매 프레임
            // 끌기가 해제된다. 시체의 끌기 종료는 E(풀기)·거리 끊김·운반자 소실뿐이다. (#571)
            if (!npc.Death.IsDead && npc.CurrentState != NpcState.Escorted)
                ReleaseDrag(npc);
        }
    }

    /// <summary>
    /// 줄을 계속 걸어 둘 수 있는 대상인가 — 커스터디(연행·체포) <b>또는 시체</b>. (#571)
    ///
    /// 시체가 예외인 이유는 <b>커스터디를 쓰지 않기 때문</b>이다. 산 대상의 줄은 <c>Escorted</c>를
    /// 타지만 시체는 <see cref="NpcState.Dead"/>에서 나갈 수 없어 그 상태로 들어갈 수 없고, 들어갈
    /// 이유도 없다 — 시체는 신병이 아니라 짐이다(유치장 문 앞에서 계상되지만 그 경로도 커스터디를 안 쓴다).
    ///
    /// ⚠ 그래서 시체만은 상태가 아니라 <b>줄이 실제로 걸려 있는지</b>로 가른다. "죽었으면 무조건
    /// 유지"로 두면 <b>끌던 대상이 손 안에서 죽는 경로</b>가 새어 나간다: 사망 진입이 줄을 전부
    /// 끊는데(<see cref="NpcDeath.ServerEnterDead"/> ④ <c>ServerClearDrag</c>) 이 목록에는 남아,
    /// 플레이어가 있지도 않은 줄에 밧줄 칸을 영영 물린 채 아무것도 못 묶게 된다.
    /// 그 경로로 죽은 시체는 <b>다시 묶어야</b> 끌 수 있다 — 산 대상의 줄(위치 대입)과 시체의
    /// 줄(관절)은 다른 물건이라 이어 붙지 않는다.
    /// </summary>
    private static bool IsTetherableState(NpcController npc) =>
        npc.Death.IsDead
            ? npc.Rope.IsTethered
            : npc.CurrentState == NpcState.Escorted || npc.CurrentState == NpcState.Captured;

    // 끊김 판정 — 수평 거리만 본다(끌기 장력과 같은 기준, 계단·경사에서 y차로 오작동하지 않게).
    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    // ---- 목줄 판정 (#398) — 실제 속도 제한은 RopeDragLoad가 한다 ----

    /// <summary>
    /// 이 대상의 밧줄이 지금 <b>목줄</b>로 나를 붙잡는가 — 끊김 판정(서버)과 이동 제한(오너)이 같은 기준을
    /// 봐야 해서 한 곳에 둔다. 전 피어에서 유효. (#398)
    ///
    /// 혼자 끌 때는 걸지 않는다 — 걸면 벽에 걸린 대상을 흘리고 갈 수 없어져 "막혀서 못 감"이 유일한
    /// 결말이 된다. 놓아둔 줄도 제외 — 늘어나다 끊기는 것이 손을 떼는 수단이다.
    /// </summary>
    internal bool IsLeashedTo(NpcController npc) =>
        IsDraggingNpc(npc) && npc.Rope.DraggerCount >= k_leashDraggerCount;

    // ---- 놓기 (서버·오프라인 전용) ----

    /// <summary>밧줄 끌기 놓기 — 지정한 NPC 하나만 그 자리에 풀어 체포(Captured) 상태로 세운다(에이전트 복구).
    /// 서버(또는 오프라인) 실행. 나머지 대상은 계속 끌린다.
    /// <b>밧줄은 풀리지 않는다</b> — 줄은 여전히 이 플레이어와 이어져 있고 다시 E로 끌 수 있다.
    /// 실제로 푸는 건 밧줄 좌클릭 채널링(<see cref="PlayerEscortCommands"/>)뿐이다.</summary>
    public void ReleaseDrag(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return;
        // 파괴된 대상은 건드리지 않는다 — 아래에서 NPC 쪽 상태를 직접 묻는다 (라운드 종료 정리 경로)
        if (npc == null)
            return;
        if (!m_tethered.Contains(npc) || !npc.Rope.IsDraggedBy(transform))
            return; // 안 묶었거나 이미 놓은 대상

        // 내 앵커만 뺀다 — 남이 함께 끌고 있으면(줄다리기) 대상은 계속 끌린다.
        bool stillDragged = npc.Rope.StopRopeDrag(transform); // 놓은 자리가 NavMesh 밖이면 이 플레이어가 선 자리로 대체 복귀
        SetTetherDragging(npc, false);

        // 내가 커스터디 장부의 주인이었으면 남은 참가자에게 넘긴다 — 안 넘기면 손 뗀 사람이 계속
        // EscortTarget으로 남는다(#643).
        Transform successor = stillDragged ? npc.Rope.AnyDragger : null;
        bool handedOver = npc.Custody.HandOverEscortTarget(transform, successor);

        Feedback?.NotifyOwner(
            stillDragged
                ? $"밧줄 끌기 놓기: {npc.name} — 다른 참가자가 계속 끌고 있다 (줄은 그대로)"
                    + (handedOver ? $" · 커스터디를 {successor.name}에게 넘겼다" : "")
                : $"밧줄 끌기 놓기: {npc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");

        // 아직 아무도 안 끌고 커스터디면 그 자리에서 Captured로 멈춘다(방치 타이머·재확보로 이어짐).
        // 이미 다른 상태로 넘어갔으면(판정 후 수감·넉백·페널티) 그 행선지를 덮어쓰지 않는다. (#230)
        if (!stillDragged && npc.CurrentState == NpcState.Escorted)
            npc.Custody.StopEscort();
    }

    /// <summary>끌고 있는 대상 전부를 놓는다 — 디스폰 등 플레이어가 사라지는 경로 전용. 줄은 유지된다.</summary>
    public void ReleaseAllDrags()
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_tethered.Count - 1; i >= 0; i--)
            ReleaseDrag(m_tethered[i]);
    }

    public override void OnNetworkDespawn()
    {
        ReleaseAllDrags();
    }

    // 오너 피드백은 같은 오브젝트의 OwnerFeedback 컴포넌트가 제공한다. (#91)
}
