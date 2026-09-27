using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오검거 페널티(GDD 7-3) — 팀 오검거 카운트를 서버 권위로 누적하고, 초과 시 원한 구역 시민을 추격대로 출동시킨다.
/// 발동은 기본적으로 꺼져 있어 오검거는 집계만 되며, 호송 파이프라인은 납치 이벤트가 재사용한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public partial class WrongfulArrestPenalty : NetworkedManagerBase
{
    private const int k_maxWrongful = 1;
    private const float k_hangSeconds = 30f;

    private const float k_carrierGap = 1.1f;
    private const float k_plazaArriveDistance = 2f;
    private const float k_carryTravelTimeoutSeconds = 90f;
    private const float k_warningSeconds = 8f;
    private const float k_detentionSlotSpacing = 1.1f;

    [Header("페널티 발동 (#612)")]
    [Tooltip(
        "끄면 원한 구역 수용·추격대 출동·호송·광장 매달기를 전부 하지 않고 오검거 시민을 그 자리에서 석방한다. "
            + "오검거 집계(정산 '최다 오검거')는 켜짐/꺼짐과 무관하게 그대로 쌓인다"
    )]
    [SerializeField]
    private bool m_penaltyEnabled;

    [Header("광장 (매달기 지점) — 비우면 원점")]
    [Tooltip("페널티 확정 시 끌려가/이송될 맵 중앙 지점. 씬의 빈 GameObject를 지정한다")]
    [SerializeField]
    private Transform m_plazaPoint;

    [Header("원한 구역 (오검거 시민 수용 지점) — 비우면 그 자리 수용 (#277)")]
    [Tooltip(
        "오검거당한 시민이 걸어가 대기하는 지점. NavMesh 위에 둘 것 — 여러 명은 이 지점 주변으로 퍼져 선다"
    )]
    [SerializeField]
    private Transform m_detentionPoint;

    [Header("추격 (#278)")]
    [Tooltip("격퇴(RepelChasers, 호루라기 #250 예정)가 미치는 반경(m)")]
    [SerializeField]
    private float m_repelRadius = 10f;

    [Header("수렴·호송 (#279)")]
    [Tooltip("포획된 플레이어 주변 이 거리(m) 안에 전원이 모이면 호송을 시작한다")]
    [SerializeField]
    private float m_convergeArriveDistance = 2.5f;

    [Tooltip(
        "수렴 대기 상한(초) — 길이 막힌 NPC가 있어도 이 시간이 지나면 모인 인원으로 호송을 시작한다"
    )]
    [SerializeField]
    private float m_convergeTimeoutSeconds = 20f;

    private ArrestJudge Judge => App.Game.ArrestJudge;

    private readonly NetworkVariable<int> m_teamCountSynced = new NetworkVariable<int>();

    private readonly Dictionary<ulong, int> m_perPlayerCounts = new Dictionary<ulong, int>();

    private readonly List<NpcController> m_detained = new List<NpcController>();

    private readonly List<NpcController> m_activeNpcs = new List<NpcController>();

    private Transform m_carryTarget;

    public int TeamWrongfulCount => m_teamCountSynced.Value;

    public IReadOnlyDictionary<ulong, int> PerPlayerCounts => m_perPlayerCounts;

    /// <summary>라운드 사이 초기화 — 개인 오검거 집계만 비운다(팀 카운트는 별개 성격이라 안 건드림). 서버(또는 오프라인) 전용.</summary>
    public void ServerResetRound()
    {
        if (IsSpawned && !IsServer)
            return;

        m_perPlayerCounts.Clear();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_teamCountSynced.Value = 0;
            m_perPlayerCounts.Clear();
            m_detained.Clear();
            m_activeNpcs.Clear();
            m_carryTarget = null;

            if (Judge != null)
                Judge.OnArrestJudged += HandleArrestJudged;
            else
                Debug.LogWarning(
                    "WrongfulArrestPenalty: ArrestJudge를 찾지 못해 오검거를 집계할 수 없다",
                    this
                );
        }
    }

    public override void OnNetworkDespawn()
    {
        if (Judge != null)
            Judge.OnArrestJudged -= HandleArrestJudged;
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        if (result.Verdict != ArrestVerdict.WrongfulArrest)
            return;

        foreach (PlayerEscorter deliverer in result.DeliveredBy)
        {
            if (deliverer == null)
                continue;

            ulong clientId = deliverer.OwnerClientId;
            m_perPlayerCounts.TryGetValue(clientId, out int prev);
            m_perPlayerCounts[clientId] = prev + 1;
        }

        if (!m_penaltyEnabled)
        {
            Debug.Log(
                $"[오검거] 집계만 — 페널티 발동 꺼짐(#612), 석방한다. {FormatPerPlayerCounts()}"
            );
            if (result.Npc != null)
                result.Npc.Custody.ReleaseFromCustody();
            return;
        }

        m_teamCountSynced.Value += 1;

        Debug.Log($"[오검거] 팀 카운트 {m_teamCountSynced.Value} — {FormatPerPlayerCounts()}");

        DetainNpc(result.Npc);

        if (m_teamCountSynced.Value > k_maxWrongful)
            LaunchSquad(CollectTargets(result.DeliveredBy));
    }

    /// <summary>시체 오검거를 인계자 전원 기준으로 집계한다. 서버 전용.</summary>
    public void ServerCountWrongfulCorpse(List<PlayerEscorter> deliverers)
    {
        if (IsSpawned && !IsServer)
            return;

        if (deliverers != null)
        {
            foreach (PlayerEscorter deliverer in deliverers)
            {
                if (deliverer == null)
                    continue;

                ulong clientId = deliverer.OwnerClientId;
                m_perPlayerCounts.TryGetValue(clientId, out int prev);
                m_perPlayerCounts[clientId] = prev + 1;
            }
        }

        if (!m_penaltyEnabled)
        {
            Debug.Log(
                $"[오검거] 시체 인계 — 집계만, 페널티 발동 꺼짐(#612). {FormatPerPlayerCounts()}"
            );
            return;
        }

        m_teamCountSynced.Value += 1;
        Debug.Log(
            $"[오검거] 시체 인계 — 팀 카운트 {m_teamCountSynced.Value} — {FormatPerPlayerCounts()}"
        );

        if (m_teamCountSynced.Value > k_maxWrongful)
            LaunchSquad(CollectTargets(deliverers));
    }

    private static List<Transform> CollectTargets(List<PlayerEscorter> deliverers)
    {
        var targets = new List<Transform>();
        if (deliverers == null)
            return targets;

        foreach (PlayerEscorter deliverer in deliverers)
            if (deliverer != null)
                targets.Add(deliverer.transform);

        return targets;
    }

    private void DetainNpc(NpcController npc)
    {
        if (npc == null)
            return;

        if (m_detentionPoint == null)
            Debug.LogWarning(
                "WrongfulArrestPenalty: 원한 구역(Detention Point) 미배선 — 그 자리에서 수용된다",
                this
            );

        Vector3 slotOffset = GatherSlot.Offset(m_detained.Count, k_detentionSlotSpacing);

        m_detained.Add(npc);
        npc.Penalty.SendToDetention(m_detentionPoint, slotOffset);
        Debug.Log($"[오검거] 원한 구역 수용: {npc.name} — 대기 {m_detained.Count}명");
    }

    private void LaunchSquad(List<Transform> targets)
    {
        m_teamCountSynced.Value = 0;
        PruneDead(m_detained);

        targets ??= new List<Transform>();
        targets.RemoveAll(t => t == null);

        if (m_detained.Count == 0)
        {
            Debug.LogWarning("[오검거] 원한 구역이 비어 있음 — 텔레포트 집행 폴백", this);
            foreach (Transform target in targets)
                HangAsync(target).Forget();
            return;
        }

        int launched = m_detained.Count;
        foreach (NpcController npc in m_detained)
        {
            m_activeNpcs.Add(npc);
            npc.Penalty.OnPenaltyCaught += HandlePenaltyCaught;
            npc.Penalty.StartPenaltyChase(NearestTarget(npc.transform.position, targets));
        }
        m_detained.Clear();

        foreach (Transform target in targets)
            ShowWarning(target, k_warningSeconds);

        string targetNames =
            targets.Count > 0
                ? string.Join(", ", targets.ConvertAll(t => t.name))
                : "(없음 — 사냥 모드)";
        Debug.Log($"[오검거] 추격대 출동 — {launched}명, 초기 타겟 {targetNames}");
    }

    private static Transform NearestTarget(Vector3 from, List<Transform> targets)
    {
        if (targets == null || targets.Count == 0)
            return null;

        Transform nearest = targets[0];
        float best = (nearest.position - from).sqrMagnitude;
        for (int i = 1; i < targets.Count; i++)
        {
            float distance = (targets[i].position - from).sqrMagnitude;
            if (distance >= best)
                continue;

            best = distance;
            nearest = targets[i];
        }

        return nearest;
    }

    /// <summary>user 주변 추격 NPC를 격퇴해 재추격 쿨다운을 건다. 서버(또는 오프라인) 전용.</summary>
    public void RepelChasers(Transform user)
    {
        if (IsSpawned && !IsServer)
            return;
        if (user == null)
            return;

        PruneDead(m_activeNpcs);
        foreach (NpcController npc in m_activeNpcs)
        {
            if (Vector3.Distance(npc.transform.position, user.position) <= m_repelRadius)
                npc.Penalty.ApplyChaseRepel(user);
        }
    }

    private async UniTask HangAsync(Transform target)
    {
        if (target == null)
            return;

        PlayerMovement movement = target.GetComponent<PlayerMovement>();
        PlayerIncapacitation incap = target.GetComponent<PlayerIncapacitation>();

        if (incap != null && incap.IsIncapacitated && incap.Cause != IncapacitationCause.Penalty)
        {
            Debug.Log(
                $"[오검거] 매달기 건너뜀 — {target.name}은 이미 {incap.Cause} 상태다 (그쪽이 우선)"
            );
            return;
        }

        if (m_plazaPoint == null)
            Debug.LogWarning(
                "WrongfulArrestPenalty: Plaza Point 미할당 — 원점(0,0,0)으로 이송된다. 인스펙터에 광장 지점을 지정할 것",
                this
            );

        Vector3 pos = m_plazaPoint != null ? m_plazaPoint.position : Vector3.zero;
        Quaternion rot = m_plazaPoint != null ? m_plazaPoint.rotation : Quaternion.identity;

        if (movement != null)
            movement.ServerTeleport(pos, rot);
        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Penalty);

        Debug.Log($"[오검거] 광장 매달기 — {target.name} → {pos}, {k_hangSeconds}초");

        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(k_hangSeconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (incap != null && incap.Cause == IncapacitationCause.Penalty)
            incap.Recover();
    }

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);

    private static void ShowWarning(Transform target, float seconds)
    {
        if (target == null)
            return;

        PlayerPenaltyView view = target.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.ShowWarning(seconds);
    }

    private string FormatPerPlayerCounts()
    {
        if (m_perPlayerCounts.Count == 0)
            return "개인집계 없음";

        var sb = new System.Text.StringBuilder("개인집계 [");
        bool first = true;
        foreach (KeyValuePair<ulong, int> pair in m_perPlayerCounts)
        {
            if (!first)
                sb.Append(", ");
            sb.Append($"client {pair.Key}:{pair.Value}회");
            first = false;
        }
        sb.Append(']');
        return sb.ToString();
    }
}
