using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 — 검거된 범인을 실제로 수용·관리하는 구역. (GDD 7-2, #228)
/// 판정 구역(HqDropoffZone/ArrestJudge)과 분리되어 있다: 판정은 "누가 범인인가"만,
/// 여기는 "어디에 가두고 몇 명이 있는가"만 안다. 둘을 잇는 건 CustodyRouter다.
///
/// 수용 인원은 서버 권위로 세어 NetworkVariable로 전 피어에 동기화한다 (#56 패턴) —
/// 본부 UI(별도 이슈)는 InmateCount/OnInmateCountChanged를 읽으면 된다.
/// 자물쇠·탈출(별도 이슈)은 ReleaseInmate로 이 카운트에서 빠져나간다.
/// </summary>
public class JailZone : NetworkBehaviour
{
    [Header("수용 지점 (비우면 유치장 자신의 위치)")]
    [Tooltip("수감된 NPC가 걸어가 서는 지점들. 순서대로 배정된다 — NavMesh 위에 둘 것")]
    [SerializeField] private Transform[] m_cellPoints;

    [Header("출구 지점 (비우면 유치장 자신의 위치)")]
    [Tooltip("탈옥으로 방출된 수감자를 옮길 유치장 밖 지점 — 창살 안에 갇히지 않게 한다 (#415). 문 바깥 NavMesh 위에 둘 것")]
    [SerializeField] private Transform m_exitPoint;

    [Header("자물쇠 (비우면 같은 오브젝트에서 자동 탐색)")]
    [Tooltip("새 수감자를 받을 때 자동으로 다시 잠근다 — 범인 탈출 이벤트(#231)로 열린 상태를 되돌리는 경로")]
    [SerializeField] private JailLock m_jailLock;

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

        public InmateRecord(int bounty, bool isCriminal)
        {
            Bounty = bounty;
            IsCriminal = isCriminal;
        }
    }

    // 수용 지점 순차 배정 커서 — 여러 명이 한 점에 겹쳐 서지 않게 돌려 쓴다
    private int m_nextCellIndex;

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
        // 자물쇠는 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailLock == null)
            m_jailLock = GetComponent<JailLock>();
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
    /// 수용 지점 배정 — 수감 대상 1명이 걸어갈 지점을 내준다. 서버(또는 오프라인)에서 호출.
    /// 지점 수보다 많이 들어오면 앞에서부터 돌려 쓴다 — 겹친 NPC는 NavMesh 회피가 흩어 준다.
    /// </summary>
    public Transform ReserveCell()
    {
        if (m_cellPoints == null || m_cellPoints.Length == 0)
            return transform;

        // 인스펙터에서 비워 둔 슬롯은 건너뛴다
        for (int i = 0; i < m_cellPoints.Length; i++)
        {
            Transform cell = m_cellPoints[m_nextCellIndex % m_cellPoints.Length];
            m_nextCellIndex = (m_nextCellIndex + 1) % m_cellPoints.Length;

            if (cell != null)
                return cell;
        }

        return transform;
    }

    /// <summary>
    /// 수용 — 검거 판정 직후 CustodyRouter가 호출한다(NPC가 셀까지 걸어 도착하기를 기다리지 않는다).
    /// 판정 순간 바로 세므로 할당량 종료(#340)가 카운트를 앞질러 마지막 검거가 정산에서 누락되지 않는다.
    /// 탈옥해 풀려난 대상은 ReleaseInmate로 이 카운트에서 빠지므로 "끝까지 데리고 있어야 보상"은 유지된다.
    /// <paramref name="bounty"/>는 이 수감자가 라운드 종료 정산(#340)에 기여할 보상액이다(CustodyRouter가 판정 보상을 넘긴다).
    /// </summary>
    public void Admit(NpcController npc, int bounty)
    {
        if (npc == null)
            return;

        // 카운트는 서버 권위 — 클라이언트에서 불려도 무시한다
        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Add(npc))
            return; // 이미 수용됨 — 중복 통보 무시

        // 진범 여부를 수감 시점에 판정해 박제한다 — 정산 때 살아 있는 NPC를 다시 안 봐도 되게 (#358).
        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc)); // 재수용 시 최신 값으로 갱신
        SetInmateCount(m_inmates.Count);
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");

        // 탈출 이벤트(#231)로 열린 자물쇠는 새 수감자를 받는 순간 자동으로 다시 잠긴다 —
        // 플레이어가 따로 잠글 것이 없으면서도 연속 발동은 자연히 막힌다.
        // 유치장이 자물쇠를 아는 방향이다(그 반대가 아니라) — 자물쇠는 수용을 몰라야 한다.
        if (m_jailLock != null)
            m_jailLock.ServerRelock();
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
