using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 밧줄을 끄는 대가 — 끄는 무게로 이동 속도를 늦추고, 여럿이 끌 때는 목줄 반경으로 이동을 제한한다.
/// 연결 상태는 PlayerEscorter의 것을 빌려 읽는다.
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
public class RopeDragLoad : NetworkBehaviour
{
    [Header("무게 페널티 — #398")]
    [Tooltip(
        "끌고 있는 무게 1당 깎이는 이동속도 비율 — 표준 무게(1.0) 1명을 혼자 끌면 이만큼 느려진다. 무게 자체는 NpcCommonConfig에서 추첨된다"
    )]
    [SerializeField]
    private float m_dragSlowPerWeight = 0.25f;

    [Tooltip(
        "무게 페널티 하한(배율) — 아무리 무거워도 이 아래로는 느려지지 않는다. 0으로 두면 이동이 완전히 막힐 수 있다"
    )]
    [SerializeField]
    private float m_minDragSpeedFactor = 0.35f;

    [Tooltip("기능 정지된 동료 1명을 운반할 때의 무게 — NPC 표준 무게(1.0)와 같다")]
    [SerializeField]
    private float m_carriedPlayerWeight = 1f;

    [Tooltip("초중량 개체(밀수 운반책)를 끌 때 쓰는 속도 하한 — 혼자서는 사실상 못 끌게 한다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_heavyDragSpeedFactor = 0.04f;

    private float m_dragSpeedFactor = 1f;

    private readonly NetworkVariable<float> m_dragSpeedFactorSynced = new NetworkVariable<float>(
        1f
    );

    public float DragSpeedFactor =>
        IsSpawned && !IsServer ? m_dragSpeedFactorSynced.Value : m_dragSpeedFactor;

    private PlayerEscorter m_escorter;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

    /// <summary>팽팽한 밧줄이 허용하는 범위로 수평 이동 속도를 깎아 돌려준다(오너 로컬).</summary>
    public Vector3 ConstrainByTautRopes(Vector3 horizontalVelocity)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
            return horizontalVelocity;

        int count = escorter.TetheredCount;
        for (int i = 0; i < count; i++)
        {
            NpcController npc = escorter.GetTetheredNpc(i);
            if (npc == null || !escorter.IsLeashedTo(npc))
                continue;

            Vector3 toNpc = npc.transform.position - transform.position;
            toNpc.y = 0f;
            float distance = toNpc.magnitude;
            if (distance < 0.001f)
                continue;

            float radius = escorter.RopeBreakDistance / Mathf.Max(1, npc.Rope.DraggerCount);
            if (distance < radius)
                continue;

            Vector3 outward = -toNpc / distance;
            float away = Vector3.Dot(horizontalVelocity, outward);
            if (away > 0f)
                horizontalVelocity -= outward * away;
        }

        horizontalVelocity = ConstrainByCarriedBody(horizontalVelocity);

        return horizontalVelocity;
    }

    private Vector3 ConstrainByCarriedBody(Vector3 horizontalVelocity)
    {
        PlayerCarrier carrier = Escorter.CarriedPlayer;
        PlayerCarrier body = carrier != null ? carrier.CarriedBody : null;
        if (body == null || body.CarrierCount < 2)
            return horizontalVelocity;

        Vector3 toBody = body.transform.position - transform.position;
        toBody.y = 0f;
        float distance = toBody.magnitude;
        if (distance < 0.001f)
            return horizontalVelocity;

        float radius = carrier.BreakDistance / body.CarrierCount;
        if (distance < radius)
            return horizontalVelocity;

        Vector3 outward = -toBody / distance;
        float away = Vector3.Dot(horizontalVelocity, outward);
        if (away > 0f)
            horizontalVelocity -= outward * away;

        return horizontalVelocity;
    }

    /// <summary>끌리는 대상에 자리 번호를 매기고 무게 페널티를 계산한다. 서버(또는 오프라인) 전용.</summary>
    internal void ServerTickWeight()
    {
        IReadOnlyList<NpcController> tethered = Escorter.ServerTethered;

        int draggingCount = 0;
        for (int i = 0; i < tethered.Count; i++)
            if (tethered[i].Rope.IsDraggedBy(transform))
                draggingCount++;

        int slot = 0;
        float weightSum = 0f;

        bool draggingHeavy = false;

        for (int i = 0; i < tethered.Count; i++)
        {
            NpcController npc = tethered[i];
            if (!npc.Rope.IsDraggedBy(transform))
                continue;

            npc.Rope.SetDragSlot(slot, draggingCount);
            slot++;

            draggingHeavy |= npc.Rope.IgnoresDragSpeedFloor;

            weightSum += npc.Rope.DragWeight / Mathf.Max(1, npc.Rope.DraggerCount);
        }

        PlayerCarrier carriedBody = Escorter.CarriedPlayer?.CarriedTarget;
        if (carriedBody != null)
            weightSum += m_carriedPlayerWeight / Mathf.Max(1, carriedBody.CarrierCount);

        float floor = draggingHeavy ? m_heavyDragSpeedFactor : m_minDragSpeedFactor;

        SetDragSpeedFactor(Mathf.Clamp(1f - m_dragSlowPerWeight * weightSum, floor, 1f));
    }

    private void SetDragSpeedFactor(float factor)
    {
        m_dragSpeedFactor = factor;
        if (IsSpawned && IsServer)
            m_dragSpeedFactorSynced.Value = factor;
    }
}
