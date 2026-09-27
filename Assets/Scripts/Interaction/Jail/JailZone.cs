using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감옥 — 수감자 수용·배치 지점·정산 원장을 관리한다(GDD 7-2). 출입은 JailIntake가 맡는다.
/// 수용 인원은 서버 권위로 세어 NetworkVariable로 동기화한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailZone : NetworkedManagerBase
{
    [Header("수감자 배치 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip(
        "수감자를 세울 지점들. 빈 자리를 앞에서부터 배정한다 — 감옥 방 NavMesh 위, 문 앞 동선을 비켜 둘 것. "
            + "Z축(파랑 화살표)이 서서 바라보는 방향이다.\n\n"
            + "순간이동으로 배치되므로 걸어갈 경로는 필요 없지만, 배회·도주가 이 자리에서 이어지려면 "
            + "NavMesh 위여야 한다"
    )]
    [SerializeField] private Transform[] m_inmatePoints;

    [Header("플레이어 입장 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip("문에 E를 눌러 들어온 플레이어가 서는 자리 — 감옥 방 안. 배치 지점과 겹치지 않게 둘 것")]
    [SerializeField] private Transform m_playerEntryPoint;

    [Header("퇴장 지점 (비우면 감옥 자신의 위치)")]
    [Tooltip(
        "셀에서 나오는 플레이어·반출 대상이 서는 자리 — <b>철창문 안쪽 본관 실내</b>의 NavMesh(HQ 영역) "
            + "위에 둘 것 (#744). 탈옥으로 방출된 수감자도 여기로 나온 뒤 본부를 가로질러 도시로 걸어 나간다.\n\n"
            + "Z축(파랑 화살표)이 <b>본관 안쪽</b>을 보게 둘 것 — 여럿이 한 번에 나올 때 그 방향으로 "
            + "줄이 늘어난다 (ExitSlot). 반대로 두면 자리가 철창 너머로 파고든다"
    )]
    [SerializeField] private Transform m_exitPoint;

    [Header("본부 정문 (비우면 자동 개폐 없음)")]
    [Tooltip(
        "방출·반출 대상이 도시로 나갈 때 열어 줄 본부 정문 (#744). 안 열면 닫힌 문짝을 그대로 "
            + "통과한다 — 문짝 콜라이더는 CharacterController만 막고 NavMeshAgent는 지나간다.\n\n"
            + "닫는 것은 플레이어 몫이다 (E 토글)"
    )]
    [SerializeField] private DoubleDoor[] m_frontDoors;

    [Header("감옥 방 범위 (비우면 자식에서 자동 탐색)")]
    [Tooltip(
        "'이 좌표가 감옥 안인가'를 답하는 부피 — 문 E가 들어가기/나오기를 가르는 유일한 기준이다. "
            + "방 전체를 덮되 도시 쪽과 겹치지 않게 둘 것. Is Trigger를 켜 둘 것(끄면 플레이어를 막는다)"
    )]
    [SerializeField] private BoxCollider m_roomVolume;

    private readonly NetworkVariable<int> m_inmateCount = new NetworkVariable<int>(0);

    private int m_localInmateCount;

    private readonly NetworkVariable<int> m_bountyTotal = new NetworkVariable<int>(0);
    private int m_localBountyTotal;

    private readonly HashSet<NpcController> m_inmates = new HashSet<NpcController>();

    private readonly HashSet<NpcController> m_corpses = new HashSet<NpcController>();

    private readonly Dictionary<NpcController, InmateRecord> m_records = new Dictionary<NpcController, InmateRecord>();

    private readonly struct InmateRecord
    {
        public readonly int Bounty;
        public readonly bool IsCriminal;

        public readonly ulong[] Deliverers;

        public InmateRecord(int bounty, bool isCriminal, ulong[] deliverers)
        {
            Bounty = bounty;
            IsCriminal = isCriminal;
            Deliverers = deliverers;
        }
    }

    private NpcController[] m_placementOccupants;

    private int m_overflowCursor;

    public int InmateCount => IsSpawned ? m_inmateCount.Value : m_localInmateCount;

    public IReadOnlyCollection<NpcController> Inmates => m_inmates;

    public Transform ExitPoint => m_exitPoint != null ? m_exitPoint : transform;

    /// <summary>본부 정문을 열어 셀에서 나온 대상이 도시로 나갈 길을 튼다(잠긴 문 제외). 서버(또는 오프라인) 전용.</summary>
    public void ServerOpenFrontDoors()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_frontDoors == null)
            return;

        for (int i = 0; i < m_frontDoors.Length; i++)
            if (m_frontDoors[i] != null && !m_frontDoors[i].IsLocked)
                m_frontDoors[i].ServerSetOpen(true);
    }

    public Transform PlayerEntryPoint => m_playerEntryPoint != null ? m_playerEntryPoint : transform;

    /// <summary>입장 지점 둘레의 index번째 자리를 돌려준다. 0번은 입장 지점 자체다.</summary>
    public Vector3 PlayerEntrySlot(int index)
    {
        Transform entry = PlayerEntryPoint;
        if (index <= 0)
            return entry.position;

        int row = (index + 1) / 2;
        float side = (index % 2 == 1) ? -1f : 1f;

        return entry.position
            + entry.right * (side * k_exitSlotSpacing)
            + entry.forward * (row * k_exitSlotSpacing);
    }

    /// <summary>퇴장 지점 둘레의 index번째 자리를 돌려준다. 0번은 퇴장 지점 자체다.</summary>
    public Vector3 ExitSlot(int index)
    {
        Transform exit = ExitPoint;
        if (index <= 0)
            return exit.position;

        int row = (index + 1) / 2;
        float side = (index % 2 == 1) ? -1f : 1f;

        return exit.position
            + exit.right * (side * k_exitSlotSpacing)
            + exit.forward * (row * k_exitSlotSpacing);
    }

    private const float k_exitSlotSpacing = 1.2f;

    public bool HasRoomVolume => m_roomVolume != null;

    /// <summary>좌표가 감옥 방 안인지 로컬 공간에서 판정한다. 범위 미배선이면 false.</summary>
    public bool ContainsPoint(Vector3 position)
    {
        if (m_roomVolume == null)
            return false;

        Vector3 local = m_roomVolume.transform.InverseTransformPoint(position) - m_roomVolume.center;
        Vector3 half = m_roomVolume.size * 0.5f;

        return Mathf.Abs(local.x) <= half.x
            && Mathf.Abs(local.y) <= half.y
            && Mathf.Abs(local.z) <= half.z;
    }

    /// <summary>감옥 방 안의 임의 좌표를 돌려준다(가장자리 여백 적용).</summary>
    public Vector3 RandomPointInRoom()
    {
        if (m_roomVolume == null)
            return transform.position;

        Vector3 half = m_roomVolume.size * 0.5f;
        float x = UnityEngine.Random.Range(-half.x + k_roamInset, half.x - k_roamInset);
        float z = UnityEngine.Random.Range(-half.z + k_roamInset, half.z - k_roamInset);

        Vector3 local = m_roomVolume.center + new Vector3(x, -half.y, z);
        return m_roomVolume.transform.TransformPoint(local);
    }

    private const float k_roamInset = 0.9f;

    /// <summary>시체를 눕힐, NavMesh 높이로 스냅된 방 안 임의 좌표를 돌려준다. 서버(또는 오프라인) 전용.</summary>
    public Vector3 RandomRestPointInRoom()
    {
        Vector3 point = RandomPointInRoom();

        if (UnityEngine.AI.NavMesh.SamplePosition(
                point, out UnityEngine.AI.NavMeshHit hit, k_restSnapRadius, UnityEngine.AI.NavMesh.AllAreas))
            return hit.position;

        Debug.LogWarning($"JailZone: 감옥 방 바닥을 NavMesh에서 찾지 못했다 — 시체가 바닥에 파묻힐 수 있다: {point:F2}", this);
        return point;
    }

    private const float k_restSnapRadius = 3f;

    public event Action<int> OnInmateCountChanged;

    public int BountyTotal => IsSpawned ? m_bountyTotal.Value : m_localBountyTotal;

    public event Action<int> OnBountyTotalChanged;

    protected override void Awake()
    {
        base.Awake();

        m_placementOccupants = new NpcController[m_inmatePoints != null ? m_inmatePoints.Length : 0];

        if (m_roomVolume == null)
            m_roomVolume = GetComponentInChildren<BoxCollider>();

        if (m_roomVolume == null)
            Debug.LogWarning("JailZone: 감옥 방 범위(BoxCollider)가 없다 — 문 E가 나오기를 판단하지 못한다", this);
        else if (!m_roomVolume.isTrigger)
            Debug.LogWarning($"JailZone: 방 범위({m_roomVolume.name})의 Is Trigger가 꺼져 있다 — 플레이어가 막힌다", this);
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

    /// <summary>손으로 배치한 지점 목록에서 앞에서부터 빈 자리를 배정한다. 정원 초과 시 겹쳐 세운다.</summary>
    public Transform ReservePlacement(NpcController npc)
    {
        if (npc == null || m_inmatePoints == null || m_inmatePoints.Length == 0)
            return transform;

        for (int i = 0; i < m_inmatePoints.Length; i++)
            if (m_placementOccupants[i] == npc && m_inmatePoints[i] != null)
                return m_inmatePoints[i];

        for (int i = 0; i < m_inmatePoints.Length; i++)
        {
            if (m_inmatePoints[i] == null || m_placementOccupants[i] != null)
                continue;

            m_placementOccupants[i] = npc;
            return m_inmatePoints[i];
        }

        return ShareOverflowPlacement(npc);
    }

    private Transform ShareOverflowPlacement(NpcController npc)
    {
        Transform shared = NextOverflowPlacement();
        Debug.LogWarning(
            $"[감옥] 배치 지점({m_inmatePoints.Length}개) 초과 — {npc.name}을(를) {shared.name}에 겹쳐 세운다. "
                + "정원을 늘리려면 감옥 방에 배치 지점을 추가할 것",
            this
        );
        return shared;
    }

    private Transform NextOverflowPlacement()
    {
        for (int i = 0; i < m_inmatePoints.Length; i++)
        {
            Transform spot = m_inmatePoints[m_overflowCursor % m_inmatePoints.Length];
            m_overflowCursor = (m_overflowCursor + 1) % m_inmatePoints.Length;

            if (spot != null)
                return spot;
        }

        return transform;
    }

    private void ReleasePlacement(NpcController npc)
    {
        for (int i = 0; i < m_placementOccupants.Length; i++)
            if (m_placementOccupants[i] == npc)
                m_placementOccupants[i] = null;
    }

    /// <summary>수감자를 수용하고 현상금과 인계자 clientId를 정산 원장에 기록한다.</summary>
    public void Admit(NpcController npc, int bounty, ulong[] deliverers)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Add(npc))
            return;

        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc), deliverers ?? Array.Empty<ulong>());
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");

        npc.Death.OnDied += HandleInmateDied;
    }

    /// <summary>수감 중 사망한 대상을 탈출 가능 점유에서 빼고 시체 목록으로 옮긴다.</summary>
    private void HandleInmateDied(NpcController npc, GameObject killer)
    {
        if (npc == null)
            return;

        npc.Death.OnDied -= HandleInmateDied;

        if (!m_inmates.Remove(npc))
            return;

        ReleasePlacement(npc);
        m_corpses.Add(npc);
        RefreshInmateCount();
        Debug.Log($"[유치장] 수감 중 사망: {npc.name} — 현재 {InmateCount}명 (정산 계상은 유지)");
    }

    /// <summary>시체를 정산 원장에 올린다(탈출 가능 점유에는 넣지 않는다). 서버(또는 오프라인) 전용.</summary>
    public void RecordDeceased(NpcController npc, int bounty, ulong[] deliverers)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (m_records.ContainsKey(npc))
            return;

        m_records[npc] = new InmateRecord(bounty, IsCriminalInmate(npc), deliverers ?? Array.Empty<ulong>());
        m_corpses.Add(npc);
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 사망 계상: {npc.name} — 현상금 {bounty}원, 누적 {BountyTotal}원, 수용 인원 {InmateCount}명");
    }

    /// <summary>감옥 밖으로 나간 시체를 정산 원장에서 뺀다. 서버(또는 오프라인) 전용.</summary>
    public bool ReleaseDeceased(NpcController npc)
    {
        if (npc == null)
            return false;

        if (IsSpawned && !IsServer)
            return false;

        if (m_inmates.Contains(npc))
        {
            Debug.LogWarning($"[유치장] 사망 계상 취소 실패 — 산 수감자다(ReleaseInmate를 쓸 것): {npc.name}", this);
            return false;
        }

        if (!m_records.Remove(npc))
        {
            Debug.Log($"[유치장] 사망 계상 취소 대상 아님 — 원장에 없다: {npc.name}");
            return false;
        }

        m_corpses.Remove(npc);
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 사망 계상 취소: {npc.name} — 감옥 밖으로 나갔다, 누적 현상금 {BountyTotal}원, 수용 인원 {InmateCount}명");
        return true;
    }

    /// <summary>수감자의 기록된 현상금을 돌려준다. 없으면 false. 서버(또는 오프라인) 전용.</summary>
    public bool TryGetBounty(NpcController npc, out int bounty)
    {
        bounty = 0;
        if (npc == null || !m_records.TryGetValue(npc, out InmateRecord record))
            return false;

        bounty = record.Bounty;
        return true;
    }

    /// <summary>수감자를 수용 해제하고 인원에서 뺀다(탈옥용).</summary>
    public void ReleaseInmate(NpcController npc)
    {
        if (npc == null)
            return;

        if (IsSpawned && !IsServer)
            return;

        if (!m_inmates.Remove(npc))
            return;

        npc.Death.OnDied -= HandleInmateDied;

        m_records.Remove(npc);
        ReleasePlacement(npc);
        RefreshInmateCount();
        RefreshBountyTotal();
        Debug.Log($"[유치장] 수용 해제: {npc.name} — 현재 {InmateCount}명, 누적 현상금 {BountyTotal}원");
    }

    /// <summary>정산 원장을 진범/경범죄별 인원과 보상액 합으로 집계한다. 서버(또는 오프라인) 전용.</summary>
    public (int criminals, int misdemeanors, int total) TallySettlement()
    {
        int criminals = 0;
        int misdemeanors = 0;
        int total = 0;
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

    /// <summary>수감자 현상금을 인계자들에게 균등 분배해 clientId별로 합산한다. 서버(또는 오프라인) 전용.</summary>
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

    private static bool IsCriminalInmate(NpcController npc)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        return identity != null && identity.IsCriminal;
    }

    private void RefreshBountyTotal()
    {
        int total = 0;
        foreach (InmateRecord record in m_records.Values)
            total += record.Bounty;

        m_localBountyTotal = total;

        if (IsSpawned && IsServer)
            m_bountyTotal.Value = total;
        else if (!IsSpawned)
            OnBountyTotalChanged?.Invoke(total);
    }

    private void RefreshInmateCount() => SetInmateCount(m_inmates.Count + m_corpses.Count);

    private void SetInmateCount(int value)
    {
        m_localInmateCount = value;

        if (IsSpawned && IsServer)
            m_inmateCount.Value = value;
        else if (!IsSpawned)
            OnInmateCountChanged?.Invoke(value);
    }
}
