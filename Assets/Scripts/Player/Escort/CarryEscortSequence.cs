using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// NPC 여럿이 플레이어 한 명을 붙잡아 목적지까지 끌고 가는 공용 연출 시퀀스. 서버(또는 오프라인) 전용.
/// 수렴·대형·도착 대기까지만 책임지며, 목적지와 도착 후 처리는 호출부가 정한다.
/// </summary>
public static class CarryEscortSequence
{
    public readonly struct Settings
    {
        public readonly float ConvergeArriveDistance;

        public readonly float ConvergeTimeoutSeconds;

        public readonly float CarrierGap;

        public readonly float ArriveDistance;

        public readonly float TravelTimeoutSeconds;

        public Settings(
            float convergeArriveDistance,
            float convergeTimeoutSeconds,
            float carrierGap,
            float arriveDistance,
            float travelTimeoutSeconds)
        {
            ConvergeArriveDistance = convergeArriveDistance;
            ConvergeTimeoutSeconds = convergeTimeoutSeconds;
            CarrierGap = carrierGap;
            ArriveDistance = arriveDistance;
            TravelTimeoutSeconds = travelTimeoutSeconds;
        }
    }

    private const float k_convergeSlack = 1f;

    private const float k_followSideOffset = 0.9f;
    private const float k_followFirstRowBack = 1.8f;
    private const float k_followRowGap = 1.2f;

    /// <summary>수렴 대기 → 대형 편성 → 목적지 도착 대기까지 진행한다. carriers에서 파괴된 대상은 제거한다.</summary>
    public static async UniTask<bool> RunAsync(
        Transform target,
        List<NpcController> carriers,
        Transform destination,
        Settings settings,
        CancellationToken token)
    {
        if (target == null || carriers == null || carriers.Count == 0)
            return false;

        float convergeDeadline = Time.time + settings.ConvergeTimeoutSeconds;
        while (Time.time < convergeDeadline)
        {
            PruneDead(carriers);
            if (target == null || carriers.Count == 0)
                return false;

            if (AllWithin(carriers, target.position, settings.ConvergeArriveDistance + k_convergeSlack))
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: token);
        }

        PruneDead(carriers);
        if (target == null || carriers.Count == 0)
            return false;

        Vector3 targetPosition = target.position;
        carriers.Sort(
            (a, b) => (a.transform.position - targetPosition).sqrMagnitude
                .CompareTo((b.transform.position - targetPosition).sqrMagnitude));

        NpcController carrierA = carriers[0];
        NpcController carrierB = carriers.Count > 1 ? carriers[1] : null;

        bool collide = carrierA.Penalty.IsAbductionDuty;

        carrierA.Penalty.StartPenaltyEscort(destination, null, Vector3.zero);
        if (carrierB != null)
            carrierB.Penalty.StartPenaltyEscort(destination, carrierA, new Vector3(settings.CarrierGap, 0f, 0f));

        for (int i = 2; i < carriers.Count; i++)
        {
            float x = i % 2 == 0 ? -k_followSideOffset : k_followSideOffset;
            float z = -(k_followFirstRowBack + (i - 2) / 2 * k_followRowGap);
            carriers[i].Penalty.StartPenaltyEscort(destination, carrierA, new Vector3(x, 0f, z));
        }

        PlayerPenaltyView view = target.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.StartCarried(carrierA, carrierB != null ? carrierB : carrierA, collide);

        bool arrived = true;
        float travelDeadline = Time.time + settings.TravelTimeoutSeconds;
        NpcController boundLead = carrierA;
        NpcController boundMate = carrierB != null ? carrierB : carrierA;

        while (Time.time < travelDeadline)
        {
            if (target == null)
            {
                arrived = false;
                break;
            }
            if (destination == null)
                break;

            NpcController lead = OnDuty(carrierA) ? carrierA : (OnDuty(carrierB) ? carrierB : null);
            if (lead == null)
            {
                arrived = false;
                break;
            }

            NpcController mate = OnDuty(carrierB) && lead != carrierB ? carrierB : lead;
            if (view != null && (boundLead != lead || boundMate != mate))
            {
                boundLead = lead;
                boundMate = mate;
                view.StartCarried(lead, mate, collide);
            }

            if (Vector3.Distance(lead.transform.position, destination.position) <= settings.ArriveDistance)
                break;

            await UniTask.Delay(TimeSpan.FromSeconds(0.25), cancellationToken: token);
        }

        if (view != null)
            view.StopCarried();

        return arrived;
    }

    private static bool OnDuty(NpcController npc) =>
        npc != null && npc.Penalty.PenaltyEscortGoal != null;

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);

    private static bool AllWithin(List<NpcController> npcs, Vector3 center, float radius)
    {
        float sqr = radius * radius;
        foreach (NpcController npc in npcs)
        {
            if (npc != null && (npc.transform.position - center).sqrMagnitude > sqr)
                return false;
        }

        return true;
    }
}
