using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 밧줄 연결 상태 — 묶은 NPC 목록을 관리하고 매 프레임 커스터디 이탈·거리 끊김을 정리한다.
/// 요청·검증은 PlayerEscortCommands, 무게·목줄 대가는 RopeDragLoad가 맡는다.
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerEscorter : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    internal OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    [Header("밧줄 끌기")]
    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    internal float RopeBreakDistance => m_ropeBreakDistance;

    private readonly List<NpcController> m_tethered = new List<NpcController>();

    private readonly NetworkList<RopeTether> m_tetheredSynced = new NetworkList<RopeTether>();

    public int TetheredCount => IsSpawned && !IsServer ? m_tetheredSynced.Count : m_tethered.Count;

    private const int k_leashDraggerCount = 2;

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

    private PlayerCarrier m_carrier;

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

    internal int RopeCapacity => Loadout != null ? Loadout.RopeCount : int.MaxValue;

    private int RopesInUse => TetheredCount + (IsCarryingPlayer ? 1 : 0);

    public bool IsAtRopeCapacity => RopesInUse >= RopeCapacity;

    internal IReadOnlyList<NpcController> ServerTethered => m_tethered;

    private static readonly List<PlayerEscorter> s_instances = new List<PlayerEscorter>();

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    /// <summary>해당 NPC를 밧줄로 묶고 있는 플레이어 하나를 찾는다. 없으면 null. 서버(또는 오프라인) 전용.</summary>
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

    /// <summary>해당 NPC에 밧줄을 건 플레이어를 전부 찾는다. 서버(또는 오프라인) 전용.</summary>
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

    /// <summary>대상의 밧줄을 전부 걷고 일으켜 세운 뒤 afterStandUp을 실행한다. 서버(또는 오프라인) 전용.</summary>
    public static void ReleaseAllTethersOn(NpcController npc, System.Action afterStandUp)
    {
        if (npc == null)
            return;

        List<PlayerEscorter> holders = FindEscortersOf(npc);

        for (int i = 0; i < holders.Count; i++)
            holders[i].ReleaseDrag(npc);

        npc.StandUp.ServerStandUpThen(afterStandUp);

        for (int i = 0; i < holders.Count; i++)
            holders[i].RemoveTether(npc);
    }

    /// <summary>시체에 걸린 밧줄을 일으켜 세우지 않고 전부 걷는다. 서버(또는 오프라인) 전용.</summary>
    public static void ReleaseAllTethersOnCorpse(NpcController npc) =>
        ReleaseTethersOnCorpseExcept(npc, null);

    /// <summary>시체의 밧줄 중 keeper 것만 남기고 나머지를 끊는다(null이면 전부). 서버(또는 오프라인) 전용.</summary>
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
            holder.RemoveTether(npc);

            if (keeper != null)
                holder.Feedback?.NotifyOwner($"밧줄 끊김 — 다른 참가자가 감옥 밖으로 데리고 나갔다: {npc.name}");
        }
    }

    /// <summary>나 말고 이 대상을 묶은 사람이 있는지 확인한다. 서버(또는 오프라인) 전용.</summary>
    internal bool HasOtherTether(NpcController npc)
    {
        List<PlayerEscorter> holders = FindEscortersOf(npc);
        for (int i = 0; i < holders.Count; i++)
            if (holders[i] != this)
                return true;

        return false;
    }

    /// <summary>묶인 대상을 순번으로 얻는다 — 전 피어에서 유효한 표현·검증용. 없거나 못 찾으면 null.</summary>
    public NpcController GetTetheredNpc(int index)
    {
        if (!IsSpawned || IsServer)
            return index >= 0 && index < m_tethered.Count ? m_tethered[index] : null;

        if (index < 0 || index >= m_tetheredSynced.Count)
            return null;

        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return null;

        return manager.SpawnManager.SpawnedObjects.TryGetValue(
            m_tetheredSynced[index].NpcId, out NetworkObject npcObject)
            && npcObject.TryGetComponent(out NpcController npc)
            ? npc
            : null;
    }

    /// <summary>이 NPC가 내 밧줄에 묶여 있는가 — 전 피어에서 유효. 좌클릭 분기·E 놓기 대상 판정이 쓴다.</summary>
    public bool IsTetheredTo(NpcController npc) => IndexOfTether(npc) >= 0;

    /// <summary>이 NPC를 내가 지금 끌고 있는지 확인한다(전 피어 유효).</summary>
    public bool IsDraggingNpc(NpcController npc)
    {
        if (!IsTetheredTo(npc))
            return false;

        if (!IsSpawned || IsServer)
            return npc.Rope.IsDraggedBy(transform);

        int index = IndexOfSynced(npc);
        return index >= 0 && m_tetheredSynced[index].Dragging;
    }

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

    private int IndexOfTether(NpcController npc)
    {
        if (npc == null)
            return -1;

        return !IsSpawned || IsServer ? m_tethered.IndexOf(npc) : IndexOfSynced(npc);
    }

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

    /// <summary>밧줄 연결을 맺는다. 항상 '끌고 있음'으로 시작한다.</summary>
    internal void AddTether(NpcController npc)
    {
        if (npc == null)
            return;

        if (!m_tethered.Contains(npc))
        {
            m_tethered.Add(npc);
            npc.Rope.AddTether();
        }

        SetTetherDragging(npc, true);
    }

    private void SetTetherDragging(NpcController npc, bool dragging)
    {
        if (!IsSpawned || !IsServer)
            return;

        int index = IndexOfSynced(npc);
        if (index < 0)
        {
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

    private void RemoveTetherAt(int index)
    {
        NpcController npc = m_tethered[index];
        m_tethered.RemoveAt(index);

        if (npc != null)
            npc.Rope.RemoveTether();

        if (!IsSpawned || !IsServer)
            return;

        int syncedIndex = IndexOfSynced(npc);
        if (syncedIndex >= 0)
        {
            m_tetheredSynced.RemoveAt(syncedIndex);
            return;
        }

        if (npc == null || npc.NetworkObject == null)
            PruneDeadSyncedEntries();
    }

    private void PruneDeadSyncedEntries()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return;

        for (int i = m_tetheredSynced.Count - 1; i >= 0; i--)
            if (!manager.SpawnManager.SpawnedObjects.ContainsKey(m_tetheredSynced[i].NpcId))
                m_tetheredSynced.RemoveAt(i);
    }

    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        TickTetherCleanup();
        Load?.ServerTickWeight();
    }

    /// <summary>매 프레임 커스터디 이탈·거리 끊김에 따라 밧줄 연결을 정리한다.</summary>
    private void TickTetherCleanup()
    {
        for (int i = m_tethered.Count - 1; i >= 0; i--)
        {
            NpcController npc = m_tethered[i];

            if (npc == null || !IsTetherableState(npc))
            {
                RemoveTetherAt(i);
                continue;
            }

            if (!IsLeashedTo(npc) && IsTooFarToTether(npc))
            {
                if (npc.Death.IsDead)
                {
                    ReleaseDrag(npc);
                    Feedback?.NotifyOwner($"밧줄 끊김 — 시체를 놓쳤다: {npc.name}");
                    RemoveTetherAt(i);
                    continue;
                }

                bool othersHold = HasOtherTether(npc);

                ReleaseDrag(npc);

                if (othersHold)
                {
                    RemoveTetherAt(i);
                    Feedback?.NotifyOwner($"밧줄 끊김 — 내 줄만 끊겼다 (다른 참가자가 계속 확보 중): {npc.name}");
                    continue;
                }

                if (NpcStateRules.StaysPutWhenFreed(npc))
                {
                    Feedback?.NotifyOwner($"밧줄 끊김 — 달아나지 않고 그 자리에 남는다: {npc.name}");
                    npc.StandUp.ServerStandUpThen(null);
                }
                else
                {
                    Transform threat = transform;
                    Feedback?.NotifyOwner($"밧줄 끊김 — 너무 멀어져 도주: {npc.name}");
                    npc.StandUp.ServerStandUpThen(() => npc.Reaction.ResumeReaction(threat));
                }

                RemoveTetherAt(i);
                continue;
            }

            if (!npc.Death.IsDead && npc.CurrentState != NpcState.Escorted)
                ReleaseDrag(npc);
        }
    }

    /// <summary>줄을 계속 걸어 둘 수 있는 대상(커스터디 또는 줄이 걸린 시체)인지 판정한다.</summary>
    private static bool IsTetherableState(NpcController npc) =>
        npc.Death.IsDead
            ? npc.Rope.IsTethered
            : npc.CurrentState == NpcState.Escorted || npc.CurrentState == NpcState.Captured;

    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    /// <summary>이 대상의 밧줄이 지금 목줄로 나를 붙잡는지(여럿이 끄는 중) 판정한다.</summary>
    internal bool IsLeashedTo(NpcController npc) =>
        IsDraggingNpc(npc) && npc.Rope.DraggerCount >= k_leashDraggerCount;

    /// <summary>지정한 NPC의 끌기만 놓아 Captured로 세운다(밧줄은 유지). 서버(또는 오프라인) 실행.</summary>
    public void ReleaseDrag(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return;
        if (npc == null)
            return;
        if (!m_tethered.Contains(npc) || !npc.Rope.IsDraggedBy(transform))
            return;

        bool stillDragged = npc.Rope.StopRopeDrag(transform);
        SetTetherDragging(npc, false);

        Transform successor = stillDragged ? npc.Rope.AnyDragger : null;
        bool handedOver = npc.Custody.HandOverEscortTarget(transform, successor);

        Feedback?.NotifyOwner(
            stillDragged
                ? $"밧줄 끌기 놓기: {npc.name} — 다른 참가자가 계속 끌고 있다 (줄은 그대로)"
                    + (handedOver ? $" · 커스터디를 {successor.name}에게 넘겼다" : "")
                : $"밧줄 끌기 놓기: {npc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");

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
}
