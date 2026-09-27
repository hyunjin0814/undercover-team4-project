using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감옥 출입 서비스 — 신병 판정·수감 배치, 플레이어 입·퇴장, 반출을 서버에서 처리한다.
/// 대장 역할(수용 인원·현상금)은 JailZone이 맡는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailIntake : CommonManagerBase
{
    [Header("감옥 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Tooltip(
        "판정 버튼이 신병으로 인정하는 거리(m) — 손이 빈 사람이 눌렀을 때만 쓴다. 이 안의 확보 상태"
            + "(놓아둔 Captured, 남이 끌고 온 Escorted, 내려놓은 시체)를 함께 판정하되, 아무도 손대지 "
            + "않은 대상은 빠진다. 줄을 쥐고 있으면 밧줄에 걸린 대상만 판정한다(#637). "
            + "밧줄 길이(1.6m)보다 넉넉히 둘 것"
    )]
    [SerializeField] private float m_admitReach = 4f;

    private readonly List<NpcController> m_admitBuffer = new List<NpcController>();

    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    public JailZone Zone => m_jailZone;

    protected override void Awake()
    {
        base.Awake();

        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 감옥(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);
    }

    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    /// <summary>
    /// interactor가 확보한 신병(밧줄 대상, 없으면 반경 내 확보 대상)을 판정하고 범죄자만 감옥에 넣는다.
    /// 판정을 시도한 대상 수를 돌려준다. 서버(또는 오프라인) 전용.
    /// </summary>
    public int ServerAdmitHeldBy(GameObject interactor)
    {
        if (!HasServerAuthority || interactor == null || m_jailZone == null)
            return 0;

        RoundManager round = App.Game.Round;
        if (round != null && round.Phase != RoundPhase.InProgress)
            return 0;

        CollectHeldBy(interactor);
        if (m_admitBuffer.Count == 0)
            return 0;

        if (App.Game.ArrestJudge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return 0;
        }

        PlayerEscorter presser = interactor.GetComponent<PlayerEscorter>();

        int held = m_admitBuffer.Count;
        int handled = 0;

        ArrestJudge judge = App.Game.ArrestJudge;

        for (int i = 0; i < held; i++)
        {
            NpcController npc = m_admitBuffer[i];
            if (npc == null || m_unjudgeable.Contains(npc))
                continue;

            if (npc.Death.IsDead)
            {
                bool wasDelivered = npc.Custody.IsDelivered;
                if (ServerAdmitCorpse(npc, presser))
                {
                    handled++;
                }
                else if (!wasDelivered)
                {
                    m_unjudgeable.Add(npc);
                }
                else
                {
                    Debug.Log($"[감옥] 수감 버튼 — 이미 계상된 시체: {npc.name}");
                }
                continue;
            }

            handled++;

            ArrestResult? result = judge.Judge(npc, presser);
            if (result == null)
            {
                m_unjudgeable.Add(npc);
                continue;
            }

            ulong[] deliverers = ToClientIds(result.Value.DeliveredBy);

            if (!result.Value.Verdict.IsCredited())
            {
                PlayerEscorter.ReleaseAllTethersOn(npc, null);
                Debug.Log($"[감옥] {result.Value.Verdict} — 감옥에 들이지 않고 문 앞에서 놓는다: {npc.name}");
                continue;
            }

            int bounty = result.Value.Reward;
            PlayerEscorter.ReleaseAllTethersOn(npc, null);
            ServerPlaceInJail(npc, bounty, deliverers);
        }

        m_admitBuffer.Clear();
        return handled;
    }

    /// <summary>대상을 감옥 배치 지점으로 순간이동시키고 정산에 계상한다. 서버(또는 오프라인) 전용.</summary>
    private void ServerPlaceInJail(NpcController npc, int bounty, ulong[] deliverers)
    {
        Transform spot = m_jailZone.ReservePlacement(npc);
        npc.Custody.SendToJail(spot);
        m_jailZone.Admit(npc, bounty, deliverers);

        Debug.Log($"[감옥] 수감 — {npc.name}을(를) {spot.name}에 배치했다 (현상금 {bounty}원)");
    }

    /// <summary>시체 하나를 판정하고, 계상 대상이면 감옥 안에 눕힌다. 서버(또는 오프라인) 전용.</summary>
    private bool ServerAdmitCorpse(NpcController npc, PlayerEscorter presser)
    {
        ArrestResult? result = App.Game.ArrestJudge?.JudgeCorpse(npc, presser);
        if (result == null)
            return false;

        if (!result.Value.Verdict.IsCredited())
        {
            PlayerEscorter.ReleaseAllTethersOnCorpse(npc);
            Debug.Log($"[감옥] {result.Value.Verdict} 시체 — 감옥에 들이지 않고 문 앞에 둔다: {npc.name}");
            return true;
        }

        ulong[] deliverers = ToClientIds(result.Value.DeliveredBy);
        int bounty = result.Value.Reward;

        PlayerEscorter.ReleaseAllTethersOnCorpse(npc);

        Vector3 spot = m_jailZone.RandomRestPointInRoom();
        npc.Custody.SendCorpseToJail(spot);

        m_jailZone.RecordDeceased(npc, bounty, deliverers);

        Debug.Log($"[감옥] 시체 수감 — {npc.name}을(를) 감옥 안에 눕혔다 (현상금 {bounty}원)");
        return true;
    }

    private void CollectHeldBy(GameObject interactor)
    {
        m_admitBuffer.Clear();

        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null)
        {
            for (int i = 0; i < escorter.TetheredCount; i++)
            {
                NpcController roped = escorter.GetTetheredNpc(i);
                if (roped != null && !m_admitBuffer.Contains(roped))
                    m_admitBuffer.Add(roped);
            }
        }

        if (m_admitBuffer.Count > 0)
            return;

        Vector3 origin = interactor.transform.position;
        float sqrReach = m_admitReach * m_admitReach;

        IReadOnlyList<NpcController> npcs = NpcController.All;
        for (int i = 0; i < npcs.Count; i++)
        {
            NpcController npc = npcs[i];
            if (npc == null)
                continue;
            if (!IsAdmittableState(npc.CurrentState))
                continue;
            if (npc.Custody == null || !npc.Custody.IsSecuredByAnyone)
                continue;
            if ((npc.transform.position - origin).sqrMagnitude > sqrReach)
                continue;
            if (!m_admitBuffer.Contains(npc))
                m_admitBuffer.Add(npc);
        }
    }

    /// <summary>버튼 앞에 놓아둔 것만으로 판정 대상이 되는 상태인지 판정한다.</summary>
    private static bool IsAdmittableState(NpcState state) =>
        state is NpcState.Captured or NpcState.Escorted or NpcState.Dead;

    private GameObject m_custodyProbeInteractor;
    private int m_custodyProbeFrame = -1;
    private bool m_custodyProbeResult;

    /// <summary>이 사람이 수감할 신병을 확보하고 있는지 판정한다(조준 안내 전용, 전 피어 유효).</summary>
    public bool HasAdmittableCustody(GameObject interactor)
    {
        if (interactor == null)
            return false;

        if (m_custodyProbeFrame == Time.frameCount && m_custodyProbeInteractor == interactor)
            return m_custodyProbeResult;

        m_custodyProbeFrame = Time.frameCount;
        m_custodyProbeInteractor = interactor;
        m_custodyProbeResult = ProbeCustody(interactor);
        return m_custodyProbeResult;
    }

    private bool ProbeCustody(GameObject interactor)
    {
        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null && escorter.TetheredCount > 0)
            return true;

        Vector3 origin = interactor.transform.position;
        float sqrReach = m_admitReach * m_admitReach;

        IReadOnlyList<NpcController> npcs = NpcController.All;
        for (int i = 0; i < npcs.Count; i++)
        {
            NpcController npc = npcs[i];
            if (npc == null || !IsAdmittableState(npc.CurrentState))
                continue;
            if (npc.Custody == null || !npc.Custody.IsSecuredByAnyone)
                continue;
            if ((npc.transform.position - origin).sqrMagnitude <= sqrReach)
                return true;
        }

        return false;
    }

    private static ulong[] ToClientIds(List<PlayerEscorter> deliverers)
    {
        var ids = new HashSet<ulong>();
        for (int i = 0; i < deliverers.Count; i++)
            if (deliverers[i] != null)
                ids.Add(deliverers[i].OwnerClientId);

        var result = new ulong[ids.Count];
        ids.CopyTo(result);
        return result;
    }

    /// <summary>플레이어를 감옥 안 입장 지점으로 옮긴다. 서버(또는 오프라인) 전용.</summary>
    public void ServerEnterJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform entry = m_jailZone.PlayerEntryPoint;
        mover.ServerTeleport(entry.position, entry.rotation);

        bool carried = ServerMoveCarriedBody(mover, m_jailZone.PlayerEntrySlot(1), entry.rotation);

        Debug.Log($"[감옥] 입장 — {mover.name}{(carried ? " (동료 몸 1구 동반)" : string.Empty)}");
    }

    /// <summary>플레이어를 퇴장 지점으로 옮기고, 산 동행 → 플레이어 → 밧줄 시체 순으로 함께 내보낸다.</summary>
    public void ServerExitJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform exit = m_jailZone.ExitPoint;

        mover.ServerTeleport(exit.position, exit.rotation);

        int slot = 1;
        if (ServerMoveCarriedBody(mover, m_jailZone.ExitSlot(slot), exit.rotation))
            slot++;

        int corpses = ServerExitRopedCorpses(mover, slot);
        Debug.Log($"[감옥] 퇴장 — {mover.name} (시체 {corpses}구)");
    }

    /// <summary>운반 중인 동료 몸을 함께 옮긴다. 업은 것이 없으면 false.</summary>
    private bool ServerMoveCarriedBody(PlayerMovement mover, Vector3 position, Quaternion rotation)
    {
        PlayerCarrier carrier = mover.GetComponent<PlayerCarrier>();
        PlayerCarrier body = carrier != null ? carrier.CarriedTarget : null;
        if (body == null)
            return false;

        PlayerMovement bodyMovement = body.GetComponent<PlayerMovement>();
        if (bodyMovement == null)
            return false;

        body.ServerReleaseCarriersExcept(carrier, "다른 참가자가 감옥 문으로 데리고 나갔다");
        carrier.ServerBeginTeleportGrace();
        bodyMovement.ServerTeleport(position, rotation);
        return true;
    }

    private int ServerExitRopedCorpses(PlayerMovement mover, int firstSlot)
    {
        PlayerEscorter escorter = mover.GetComponent<PlayerEscorter>();
        if (escorter == null)
            return 0;

        int moved = 0;
        for (int i = 0; i < escorter.TetheredCount; i++)
        {
            NpcController npc = escorter.GetTetheredNpc(i);
            if (npc == null || !npc.Death.IsDead)
                continue;

            PlayerEscorter.ReleaseTethersOnCorpseExcept(npc, escorter);

            npc.Custody.ServerMoveCorpse(m_jailZone.ExitSlot(firstSlot + moved));
            ServerReleaseCorpse(npc);
            moved++;
        }

        return moved;
    }

    /// <summary>문 밖으로 끌고 나온 시체를 정산 원장에서 빼고 재판정을 허용한다. 서버(또는 오프라인) 전용.</summary>
    private void ServerReleaseCorpse(NpcController npc)
    {
        if (npc == null || m_jailZone == null)
            return;

        npc.Custody.ClearDelivered();
        m_unjudgeable.Remove(npc);

        bool ledgerCleared = m_jailZone.ReleaseDeceased(npc);
        Debug.Log(
            ledgerCleared
                ? $"[감옥] 시체 반출 — 정산에서 빼고 재판정을 연다: {npc.name}"
                : $"[감옥] 시체 반출 — 원장에는 없었지만 판정 표식은 걷는다: {npc.name}"
        );
    }
}
