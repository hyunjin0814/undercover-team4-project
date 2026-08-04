using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 — 검거된 범인을 실제로 수용·관리하는 구역. (GDD 7-2, #228)
/// 판정(ArrestJudge)과 분리되어 있다: 판정은 "누가 범인인가"만, 여기는 "어디에 가두고 몇 명이
/// 있는가"만 안다. 둘을 잇고 출입을 관리하는 건 <see cref="JailIntake"/>다 (#492).
///
/// 수용 인원은 서버 권위로 세어 NetworkVariable로 전 피어에 동기화한다 (#56 패턴) —
/// 본부 UI(별도 이슈)는 InmateCount/OnInmateCountChanged를 읽으면 된다.
/// 자물쇠·탈출(별도 이슈)은 ReleaseInmate로 이 카운트에서 빠져나간다.
/// </summary>
public class JailZone : NetworkBehaviour
{
    [Header("좌석 (비우면 유치장 자신의 위치)")]
    [Tooltip(
        "수감자가 걸어가 앉는 좌석 지점들. 빈 자리를 앞에서부터 배정한다 — 벤치 위, 문↔통로 동선을 비켜, "
            + "Jail NavMesh 위에 둘 것. Z축(파랑 화살표)이 앉아서 바라보는 방향이다.\n\n"
            + "주의: 벤치 프롭에는 NavMeshModifier의 'Ignore From Build'가 켜져 있어야 한다. "
            + "끄고 NavMesh를 다시 구우면 벤치가 바닥을 파내서(카빙) 좌석이 걸어갈 수 없는 곳이 되고 "
            + "수감 이송이 전부 실패한다"
    )]
    [SerializeField] private Transform[] m_seatPoints;

    [Header("출구 지점 (비우면 유치장 자신의 위치)")]
    [Tooltip("탈옥으로 방출된 수감자를 옮길 유치장 밖 지점 — 창살 안에 갇히지 않게 한다 (#415). 문 바깥 NavMesh 위에 둘 것")]
    [SerializeField] private Transform m_exitPoint;

    // 서버 권위 수용 인원 — 서버만 쓰고 모든 클라이언트가 읽는다 (#56)
    private readonly NetworkVariable<int> m_inmateCount = new NetworkVariable<int>(0);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — NpcController의 게이지 이중 구조와 동일
    private int m_localInmateCount;

    // 현재 수감자들의 현상금 합 — 라운드 목표 금액(#395)의 라이브 진행도. 인원 수와 같은 이중 구조.
    private readonly NetworkVariable<int> m_bountyTotal = new NetworkVariable<int>(0);
    private int m_localBountyTotal;

    // 이미 수용된 NPC — 중복 카운트 방어(같은 대상이 두 번 판정·통보되거나 재수용되는 경우)
    private readonly HashSet<NpcController> m_inmates = new HashSet<NpcController>();

    // 수감자별 정산 레코드 — 보상액(bounty)과 진범 여부를 수감 시점(NPC 생존 확정)에 박제한다. 라운드 종료 시
    // 잔류 정리(MisdemeanorLoiterer)로 NPC가 파괴돼도 살아 있는 참조 없이 합산할 수 있어, 파괴 타이밍과
    // 정산 읽는 프레임의 경합으로 돌발이벤트 수감자가 누락되던 문제를 없앤다. 탈옥 방출 시 함께 제거되므로
    // 유치장에 남아 있는 대상만 계상된다("끝까지 데리고 있어야 보상"). 서버(또는 오프라인) 전용. (#358/#340)
    private readonly Dictionary<NpcController, InmateRecord> m_records = new Dictionary<NpcController, InmateRecord>();

    // 정산에 필요한 값만 담은 불변 레코드 — 수감 시점 스냅샷이라 NpcController 참조 없이 합산할 수 있다. (#358)
    private readonly struct InmateRecord
    {
        public readonly int Bounty;
        public readonly bool IsCriminal;

        /// <summary>이 수감자를 유치장에 앉힌 인계자들의 clientId — 아무도 없으면 빈 배열. (#484)
        /// 착석 시점 스냅샷이라 그 뒤 손이 바뀌어도 흔들리지 않는다.</summary>
        public readonly ulong[] Deliverers;

        public InmateRecord(int bounty, bool isCriminal, ulong[] deliverers)
        {
            Bounty = bounty;
            IsCriminal = isCriminal;
            Deliverers = deliverers;
        }
    }

    // 좌석별 점유자 — 인덱스가 m_seatPoints와 1:1이다. null이면 빈 자리. 서버(또는 오프라인) 전용. (#462)
    private NpcController[] m_seatOccupants;

    // 정원 초과분을 나눠 앉힐 좌석 커서 — 좌석이 전부 찼을 때만 쓴다 (아래 ReserveSeat)
    private int m_overflowCursor;

    /// <summary>현재 수용 인원. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public int InmateCount => IsSpawned ? m_inmateCount.Value : m_localInmateCount;

    /// <summary>현재 수감자 — 범인 탈출 이벤트(#231)가 방출 대상을 고르려고 읽는다. 서버에서만 유효.</summary>
    public IReadOnlyCollection<NpcController> Inmates => m_inmates;

    /// <summary>
    /// 방출된 수감자를 내보낼 유치장 밖 지점 — 미배선이면 유치장 자신의 위치. (#415)
    /// 유치장 내부가 시민 통행 금지 영역(Jail)이라, 방출만 하고 두면 경로가 없어 창살 안에 고착된다.
    /// </summary>
    public Transform ExitPoint => m_exitPoint != null ? m_exitPoint : transform;

    /// <summary>수용 인원 변경 — 서버·클라이언트 모든 피어에서 발생한다. 본부 UI(별도 이슈)가 구독.</summary>
    public event Action<int> OnInmateCountChanged;

    /// <summary>
    /// 지금 유치장에 잡아둔 대상들의 현상금 합 — 라운드 목표 금액(#395)의 진행도다.
    /// 라운드 종료 정산액(<see cref="TallySettlement"/>의 total)과 같은 레코드에서 나오므로
    /// 진행 중에 보이던 금액과 최종 정산이 어긋나지 않는다. 탈옥으로 방출되면 함께 줄어든다
    /// ("끝까지 데리고 있어야 인정"). 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.
    ///
    /// 팀 자금(TeamFund)과 혼동하지 말 것 — 그쪽은 세션 이월 잔액이라 상점 구매로 줄고
    /// 이전 라운드 몫이 섞여 있어 이번 라운드 진행도가 아니다.
    /// </summary>
    public int BountyTotal => IsSpawned ? m_bountyTotal.Value : m_localBountyTotal;

    /// <summary>누적 현상금 변경 — 목표 진행 HUD·라운드 종료 버튼(#395)이 구독한다. 전 피어에서 발생.</summary>
    public event Action<int> OnBountyTotalChanged;

    private void Awake()
    {
        // 좌석 점유 배열은 좌석 수와 1:1 — 좌석은 씬 배치라 런타임에 늘지 않으므로 여기서 한 번만 잡는다 (#462)
        m_seatOccupants = new NpcController[m_seatPoints != null ? m_seatPoints.Length : 0];
    }

    public override void OnNetworkSpawn()
    {
        m_inmateCount.OnValueChanged += HandleInmateCountChanged;
        m_bountyTotal.OnValueChanged += HandleBountyTotalChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_inmateCount.OnValueChanged -= HandleInmateCountChanged;
        m_bountyTotal.OnValueChanged -= HandleBountyTotalChanged;
    }

    private void HandleInmateCountChanged(int previous, int current)
    {
        OnInmateCountChanged?.Invoke(current);
    }

    private void HandleBountyTotalChanged(int previous, int current)
    {
        OnBountyTotalChanged?.Invoke(current);
    }

    /// <summary>
    /// 좌석 배정 — 수감 대상 1명이 걸어가 앉을 좌석을 내준다. 서버(또는 오프라인)에서 호출. (#462/#492)
    ///
    /// 손으로 배치한 좌석 목록에서 고른다. 자리를 계산해 만들지 않는 것이 핵심이다 —
    /// 예전 방식(셀 지점 돌려 쓰기 + GatherSlot 오프셋)은 인원이 늘면 자리가 문↔셀 통로 위에 떨어져
    /// NPC의 진입과 플레이어의 탈출을 막았다. 목록에 문 앞 자리가 없으면 그 사고가 구조적으로 불가능해진다.
    ///
    /// <paramref name="near"/>에서 <b>가장 가까운 빈 좌석</b>을 고른다 (#492). 플레이어가 신병을 내려놓은
    /// 자리가 기준이다 — 앞에서부터 채우면 방 반대편 좌석이 배정돼 걸어가는 거리가 공연히 길어진다
    /// (실측 최대 5.6m). 걸어가는 것 자체는 NpcJailedState가 한다.
    ///
    /// 정원을 넘으면 좌석을 돌려 써 겹쳐 앉힌다 — 좌석은 전부 통로 밖이라 겹쳐도 통행을 막지 않는다
    /// (팀 확정 2026-07-30). 조용히 넘어가지 않게 경고를 남긴다.
    /// </summary>
    public Transform ReserveSeat(NpcController npc, Vector3 near)
    {
        if (npc == null || m_seatPoints == null || m_seatPoints.Length == 0)
            return transform;

        // 이미 자리가 있는 대상이면 그 자리를 그대로 준다 — 재판정·중복 통보로 한 명이 두 자리를 쥐지 않게
        for (int i = 0; i < m_seatPoints.Length; i++)
            if (m_seatOccupants[i] == npc && m_seatPoints[i] != null)
                return m_seatPoints[i];

        // 빈 자리 중 기준 위치에서 가장 가까운 곳. 점유자가 파괴됐으면(라운드 종료 잔류 정리 등)
        // Unity의 null 비교가 빈 자리로 본다.
        int best = -1;
        float bestSqrDistance = float.MaxValue;
        for (int i = 0; i < m_seatPoints.Length; i++)
        {
            if (m_seatPoints[i] == null || m_seatOccupants[i] != null)
                continue;

            float sqrDistance = (m_seatPoints[i].position - near).sqrMagnitude;
            if (sqrDistance >= bestSqrDistance)
                continue;

            bestSqrDistance = sqrDistance;
            best = i;
        }

        if (best < 0)
            return ShareOverflowSeat(npc);

        m_seatOccupants[best] = npc;
        return m_seatPoints[best];
    }

    // 정원 초과 — 좌석을 돌려 써 겹쳐 앉힌다. 경고는 여기 한 곳에서만 낸다.
    private Transform ShareOverflowSeat(NpcController npc)
    {
        Transform shared = NextOverflowSeat();
        Debug.LogWarning(
            $"[유치장] 좌석 정원({m_seatPoints.Length}석) 초과 — {npc.name}을(를) {shared.name}에 겹쳐 앉힌다. "
                + "정원을 늘리려면 유치장에 벤치·좌석 지점을 추가할 것",
            this
        );
        return shared;
    }

    // 정원 초과분이 앉을 좌석 — 한 자리에 전부 몰리지 않게 커서로 나눠 준다.
    // 점유 목록에는 올리지 않는다(그 자리 주인은 먼저 앉은 수감자다) — 그 주인이 방출되면 자리는 정상적으로 빈다.
    private Transform NextOverflowSeat()
    {
        for (int i = 0; i < m_seatPoints.Length; i++)
        {
            Transform seat = m_seatPoints[m_overflowCursor % m_seatPoints.Length];
            m_overflowCursor = (m_overflowCursor + 1) % m_seatPoints.Length;

            if (seat != null)
                return seat;
        }

        return transform; // 배선된 좌석이 하나도 없다 — 유치장 자신의 위치로 폴백
    }

    // 좌석 점유 해제 — 방출·재수용으로 자리가 빈다. 비우지 않으면 정원이 조용히 줄어든다.
    private void ReleaseSeat(NpcController npc)
    {
        for (int i = 0; i < m_seatOccupants.Length; i++)
            if (m_seatOccupants[i] == npc)
                m_seatOccupants[i] = null;
    }

    /// <summary>
    /// 수용 — 검거 판정 직후 CustodyRouter가 호출한다(NPC가 셀까지 걸어 도착하기를 기다리지 않는다).
    /// 판정 순간 바로 세므로 할당량 종료(#340)가 카운트를 앞질러 마지막 검거가 정산에서 누락되지 않는다.
    /// 탈옥해 풀려난 대상은 ReleaseInmate로 이 카운트에서 빠지므로 "끝까지 데리고 있어야 보상"은 유지된다.
    /// <paramref name="bounty"/>는 이 수감자가 라운드 종료 정산(#340)에 기여할 보상액이다(CustodyRouter가 판정 보상을 넘긴다).
    /// <paramref name="deliverers"/>는 이 수감자를 앉힌 인계자들의 clientId — 개인 자금(#484)의 귀속 근거다.
    /// 착석 시점에 JailIntake가 확정해 넘긴다(판정 시점이 아니다 — 그쪽 주석 참고). 아무도 없으면 빈 배열.
    /// </summary>
    public void Admit(NpcController npc, int bounty, ulong[] deliverers)
    {
        if (npc == null)
            return;

        // 카운트는 서버 권위 — 클라이언트에서 불려도 무시한다
        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Add(npc))
            return; // 이미 수용됨 — 중복 통보 무시

        // 진범 여부를 수감 시점에 판정해 박제한다 — 정산 때 살아 있는 NPC를 다시 안 봐도 되게 (#358).
        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc), deliverers ?? Array.Empty<ulong>()); // 방출을 거친 재수용 시 최신 값으로 갱신
        SetInmateCount(m_inmates.Count);
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");

        // 자동 재잠금은 제거됐다 (#492) — 탈옥으로 열린 자물쇠는 <b>플레이어가 직접 잠가야 한다</b>
        // (유치장 문에 E). 수감만 하면 저절로 잠기던 예전 처리는 "털렸으면 가서 잠근다"는 책임을
        // 없애 버렸다. 열린 자물쇠는 문이 열린 채로 남아 계속 눈에 띈다(JailDoor.IsJailbreakHoldingOpen).
    }

    /// <summary>
    /// 이 수감자의 기록된 현상금 — 없으면 false. 서버(또는 오프라인) 전용. (#517)
    ///
    /// 반출(<see cref="JailIntake.ServerExtract"/>)이 <see cref="ReleaseInmate"/> <b>직전에</b> 읽는다.
    /// 반출은 정산에서 대상을 빼면서 판정 결과까지 잃는데, 유치장 안에서 다시 세우면 게이트를 거치지
    /// 않고 그 자리에서 재착석해야 한다 — 그때 같은 값으로 다시 계상하려고 꺼내 둔다.
    /// </summary>
    public bool TryGetBounty(NpcController npc, out int bounty)
    {
        bounty = 0;
        if (npc == null || !m_records.TryGetValue(npc, out InmateRecord record))
            return false;

        bounty = record.Bounty;
        return true;
    }

    /// <summary>
    /// 수용 해제 — 범인 탈출 이벤트(별도 이슈)가 호출할 접합점. 카운트에서 뺀다.
    /// 상태 전이(탈출 후 도주 등)는 호출자가 NpcController로 따로 처리한다.
    /// </summary>
    public void ReleaseInmate(NpcController npc)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Remove(npc))
            return;

        m_records.Remove(npc); // 방출된 수감자는 정산에서 빠진다 — 탈옥해 유치장에 없으면 보상 없음 (#340)
        ReleaseSeat(npc); // 앉아 있던 좌석을 비운다 — 다음 수감자가 그 자리에 앉을 수 있게 (#462)
        SetInmateCount(m_inmates.Count);
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용 해제: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");
    }

    /// <summary>
    /// 현재 수감자를 진범/경범죄로 나눈 인원과 보상액 합 — 라운드 종료 점유 기반 정산(#340)이 읽는다.
    /// 진범 여부는 각 수감자의 <see cref="CitizenIdentity.IsCriminal"/>로 판별한다(그 외는 경범죄 = 난동꾼·위조범).
    /// 서버(또는 오프라인) 전용 — Inmates가 서버 권위 집합이다.
    /// </summary>
    public (int criminals, int misdemeanors, int total) TallySettlement()
    {
        int criminals = 0;
        int misdemeanors = 0;
        int total = 0;
        // 저장된 레코드로 합산한다 — NPC 오브젝트가 이미 파괴됐어도(라운드 종료 잔류 정리) 계상된다. (#358)
        foreach (InmateRecord record in m_records.Values)
        {
            total += record.Bounty;
            if (record.IsCriminal)
                criminals++;
            else
                misdemeanors++;
        }
        return (criminals, misdemeanors, total);
    }

    /// <summary>
    /// clientId별 귀속 현상금 — 개인 자금 정산(#484)이 읽는다.
    /// 수감자 1명의 현상금을 그 인계자들에게 균등 분배해 합산한다(나머지 원은 버린다).
    /// 비율(10%)은 여기서 적용하지 않는다 — TallySettlement가 할당량을 떼지 않는 것과 같은 이유로, 정책은 SettlementController 몫이다.
    /// 인계자가 없는 수감자(반출 후 밧줄 없이 재수감, #492)는 아무에게도 계상되지 않는다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public Dictionary<ulong, int> TallyDelivererCredits()
    {
        var credits = new Dictionary<ulong, int>();
        foreach (InmateRecord record in m_records.Values)
        {
            if (record.Deliverers.Length == 0) continue;

            int per = record.Bounty / record.Deliverers.Length;
            if (per <= 0) continue;
            
            foreach (ulong clientId in record.Deliverers) 
                credits[clientId] = credits.TryGetValue(clientId, out int sum) ? sum + per : per;
        }

        return credits;
    }

    // 수감 시점의 진범 여부 — 기존 정산 분류와 동일 기준(CitizenIdentity.IsCriminal, 그 외는 경범죄). (#358)
    private static bool IsCriminalInmate(NpcController npc)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        return identity != null && identity.IsCriminal;
    }

    // 레코드가 바뀔 때마다 합을 다시 낸다 — 수감자 수가 많지 않아 매번 합산해도 부담이 없고,
    // 재수용으로 레코드가 통째로 교체되는 경우(Admit의 '최신 값으로 갱신')에 증분 갱신보다 안전하다.
    private void RefreshBountyTotal()
    {
        int total = 0;
        foreach (InmateRecord record in m_records.Values)
            total += record.Bounty;

        m_localBountyTotal = total;

        if (IsSpawned && IsServer)
            m_bountyTotal.Value = total; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnBountyTotalChanged?.Invoke(total);
    }

    // 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않고
    // 이벤트를 직접 발행한다 (NpcController.HandleFsmStateChanged와 동일 구조)
    private void SetInmateCount(int value)
    {
        m_localInmateCount = value;

        if (IsSpawned && IsServer)
            m_inmateCount.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnInmateCountChanged?.Invoke(value);
    }
}
