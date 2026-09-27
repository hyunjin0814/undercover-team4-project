using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 제보 전화를 받을 때마다 예비 용의자를 한 명씩 수배로 공개한다.
/// 명단과 현상금 총합은 CriminalAssigner 것을 빌려 쓴다.
/// </summary>
public sealed class SuspectRevealer
{
    private readonly IReadOnlyList<NpcController> m_suspects;

    private readonly float m_compliantWeight;
    private readonly float m_fleeWeight;
    private readonly float m_resistWeight;
    private readonly int m_bountyMin;
    private readonly int m_bountyMax;

    public SuspectRevealer(
        IReadOnlyList<NpcController> suspects,
        float compliantWeight,
        float fleeWeight,
        float resistWeight,
        int bountyMin,
        int bountyMax
    )
    {
        m_suspects = suspects;
        m_compliantWeight = compliantWeight;
        m_fleeWeight = fleeWeight;
        m_resistWeight = resistWeight;
        m_bountyMin = bountyMin;
        m_bountyMax = bountyMax;
    }

    public bool HasPending
    {
        get
        {
            for (int i = 0; i < m_suspects.Count; i++)
                if (IsPending(m_suspects[i]))
                    return true;
            return false;
        }
    }

    /// <summary>대기 중인 예비 용의자 1명을 수배로 공개한다. 공개 가능한 대상이 없으면 false.</summary>
    public bool TryPromoteNext(out int bountyDelta)
    {
        bountyDelta = 0;

        AppearanceAssigner appearance = App.Game.Appearance;
        if (appearance == null)
        {
            Debug.LogWarning("SuspectRevealer: AppearanceAssigner를 찾지 못해 승격할 수 없다");
            return false;
        }

        NpcController npc = FindNextPromotable();
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        identity.SetCriminal(true);

        npc.Custody.ClearDelivered();

        identity.AssignReaction(ReactionRoll.Roll(m_compliantWeight, m_fleeWeight, m_resistWeight));

        int promotedBounty = BountyRoll.Roll(m_bountyMin, m_bountyMax);
        bountyDelta = promotedBounty - identity.Bounty;
        identity.AssignBounty(promotedBounty);

        appearance.RevealMontage(npc);

        CitizenProfile profile = identity.Profile;
        Debug.Log(
            $"[제보 전화] 수배 공개: {(profile != null ? profile.CitizenName : npc.name)} ({identity.Reaction}, 현상금 {promotedBounty}원)"
        );
        return true;
    }

    /// <summary>미공개 예비 용의자인가 — 살아 있고, 아직 공개 전이고, 지금 잡을 수 있다.</summary>
    private static bool IsPending(NpcController npc)
    {
        if (npc == null)
            return false;

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        if (identity == null || identity.IsCriminal)
            return false;

        return npc.CurrentState != NpcState.Jailed;
    }

    /// <summary>지금 당장 승격시킬 수 있는 첫 후보. 없으면 null. (#102 설계 §3 가드)</summary>
    private NpcController FindNextPromotable()
    {
        for (int i = 0; i < m_suspects.Count; i++)
        {
            NpcController npc = m_suspects[i];
            if (!IsPending(npc))
                continue;

            if (PlayerEscorter.FindEscorterOf(npc) != null)
                continue;

            return npc;
        }
        return null;
    }
}
