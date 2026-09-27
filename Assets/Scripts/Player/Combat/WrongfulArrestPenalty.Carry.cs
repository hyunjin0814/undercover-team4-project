using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// WrongfulArrestPenalty의 호송 파트 — 포획 접수와 광장 도착 후 매달기 결말을 처리한다.
/// </summary>
public partial class WrongfulArrestPenalty
{
    private void HandlePenaltyCaught(NpcController catcher, Transform caught)
    {
        if (m_carryTarget != null)
            return;
        if (caught == null)
            return;

        PlayerIncapacitation incap = caught.GetComponent<PlayerIncapacitation>();

        if (incap != null && incap.IsIncapacitated)
            return;

        m_carryTarget = caught;

        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Penalty);

        PruneDead(m_activeNpcs);
        var convergers = new List<NpcController>(m_activeNpcs);
        foreach (NpcController npc in convergers)
            npc.Penalty.StartPenaltyConverge(caught);

        Debug.Log(
            $"[오검거] 포획 — {catcher.name} → {caught.name}, {convergers.Count}명 수렴 시작"
        );
        CarryToPlazaAsync(caught, convergers).Forget();
    }

    private async UniTask CarryToPlazaAsync(Transform caught, List<NpcController> convergers)
    {
        var settings = new CarryEscortSequence.Settings(
            m_convergeArriveDistance,
            m_convergeTimeoutSeconds,
            k_carrierGap,
            k_plazaArriveDistance,
            k_carryTravelTimeoutSeconds
        );

        bool arrived = await CarryEscortSequence.RunAsync(
            caught,
            convergers,
            m_plazaPoint,
            settings,
            destroyCancellationToken
        );

        ReleaseAll(convergers);
        m_carryTarget = null;

        if (!arrived)
        {
            Debug.Log("[오검거] 호송 중단 — 대상 소실");
            return;
        }

        if (caught != null)
            await HangAsync(caught);
    }

    private void ReleaseNpc(NpcController npc)
    {
        if (npc != null)
        {
            npc.Penalty.OnPenaltyCaught -= HandlePenaltyCaught;
            npc.Penalty.EndPenaltyDuty();
        }

        m_activeNpcs.Remove(npc);
    }

    private void ReleaseAll(List<NpcController> npcs)
    {
        for (int i = npcs.Count - 1; i >= 0; i--)
        {
            ReleaseNpc(npcs[i]);
            npcs.RemoveAt(i);
        }
    }
}
