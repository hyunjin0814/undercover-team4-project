using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 검거 판정 — 유치장 앞에서 인계된 신병(또는 시체)의 신원을 대조해 진범/경범죄/오검거를 판정한다(GDD 7-2).
/// 결과는 OnArrestJudged·OnCorpseJudged로 알린다. 서버(또는 오프라인) 전용.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ArrestJudge : CommonManagerBase
{
    private const int k_wrongfulReward = 0;

    [Tooltip("DeadOrAlive 대상을 시체로 인계할 때 깎는 비율(0.4 = 40% 감액). 100원 단위로 떨어진다(BountyRoll.Reduce). AliveOnly는 이 비율을 타지 않고 ConditionUnmet으로 0원이다 (#766)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_corpseBountyPenalty = 0.4f;

    private RoundManager Round => App.Game.Round;

    public event Action<ArrestResult> OnArrestJudged;

    private readonly Dictionary<ulong, int> m_perPlayerArrests = new Dictionary<ulong, int>();

    public IReadOnlyDictionary<ulong, int> PerPlayerArrests => m_perPlayerArrests;

    /// <summary>라운드 사이 초기화. 서버(또는 오프라인) 전용 — ShopManager 진입 지점에서 부른다.</summary>
    public void ServerResetRound()
    {
        m_perPlayerArrests.Clear();
    }

    private void CreditArrest(ArrestVerdict verdict, bool firstDelivery, List<PlayerEscorter> deliverers)
    {
        if (verdict != ArrestVerdict.WantedCriminal || !firstDelivery)
            return;

        foreach (PlayerEscorter deliverer in deliverers)
        {
            if (deliverer == null)
                continue;

            ulong clientId = deliverer.OwnerClientId;
            m_perPlayerArrests.TryGetValue(clientId, out int prev);
            m_perPlayerArrests[clientId] = prev + 1;
        }
    }

    public event Action<ArrestResult> OnCorpseJudged;

    /// <summary>산 신병의 신원을 대조해 판정을 확정하고 알린다. 서버(또는 오프라인) 전용.</summary>
    public ArrestResult? Judge(NpcController npc, PlayerEscorter presser = null)
    {
        if (npc == null) return null;
        if (npc.IsSpawned && !npc.IsServer) return null;

        if (Round != null && Round.Phase != RoundPhase.InProgress)
            return null;

        bool firstDelivery = !npc.Custody.IsDelivered;

        if (!TryResolveVerdict(npc, isCorpse: false, out ArrestVerdict verdict, out int reward, out CitizenProfile profile))
            return null;

        npc.Custody.MarkDelivered();

        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);

        if (presser != null && !deliverers.Contains(presser))
            deliverers.Add(presser);

        var result = new ArrestResult(npc, verdict, profile, reward, deliverers, firstDelivery);

        CreditArrest(verdict, firstDelivery, deliverers);
        LogVerdict(result);

        if (verdict == ArrestVerdict.WrongfulArrest)
        {
            if (deliverers.Count > 0)
            {
                foreach (PlayerEscorter deliverer in deliverers)
                    deliverer.ReleaseDrag(npc);
            }
            else
            {
                npc.Custody.StopEscort();
            }
        }

        OnArrestJudged?.Invoke(result);

        return result;
    }

    /// <summary>유치장 문 앞까지 끌고 온 시체를 판정하고 OnCorpseJudged를 발행한다. 서버(또는 오프라인) 전용.</summary>
    public ArrestResult? JudgeCorpse(NpcController npc, PlayerEscorter presser)
    {
        if (npc == null)
            return null;
        if (npc.IsSpawned && !npc.IsServer)
            return null;
        if (!npc.Death.IsDead)
            return null;

        if (Round != null && Round.Phase != RoundPhase.InProgress)
            return null;

        bool firstDelivery = !npc.Custody.IsDelivered;

        if (!TryResolveVerdict(npc, isCorpse: true, out ArrestVerdict verdict, out int reward, out CitizenProfile profile))
            return null;

        if (!firstDelivery && verdict.IsCredited())
            return null;

        if (verdict.IsCredited())
            npc.Custody.MarkDelivered();

        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);
        if (presser != null && !deliverers.Contains(presser))
            deliverers.Add(presser);

        var result = new ArrestResult(npc, verdict, profile, reward, deliverers, firstDelivery);

        CreditArrest(verdict, firstDelivery, deliverers);

        if (verdict == ArrestVerdict.WrongfulArrest)
            App.Game.WrongfulArrestPenalty?.ServerCountWrongfulCorpse(deliverers);

        LogVerdict(result);
        OnCorpseJudged?.Invoke(result);

        return result;
    }

    /// <summary>신원을 대조해 판정과 보상액을 계산하는 부수효과 없는 순수 판별.</summary>
    private bool TryResolveVerdict(
        NpcController npc,
        bool isCorpse,
        out ArrestVerdict verdict,
        out int reward,
        out CitizenProfile profile
    )
    {
        verdict = ArrestVerdict.WrongfulArrest;
        reward = k_wrongfulReward;
        profile = null;

        MisdemeanorOffender misdemeanor = npc.GetComponent<MisdemeanorOffender>();
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        if (misdemeanor == null && identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return false;
        }

        profile = identity != null ? identity.Profile : null;

        if (misdemeanor != null)
        {
            verdict = ArrestVerdict.Misdemeanor;
            reward = misdemeanor.Reward;
        }
        else if (identity.IsCriminal)
        {
            verdict = ArrestVerdict.WantedCriminal;
            reward = ResolveBounty(identity, npc);

            if (isCorpse)
            {
                if (identity.WantedCondition == WantedCondition.AliveOnly)
                {
                    verdict = ArrestVerdict.ConditionUnmet;
                    reward = 0;
                }
                else
                {
                    reward = BountyRoll.Reduce(reward, m_corpseBountyPenalty);
                }
            }
        }

        return true;
    }

    private static int ResolveBounty(CitizenIdentity identity, NpcController npc)
    {
        if (identity.Bounty <= 0)
            Debug.LogWarning($"ArrestJudge: {npc.name}에 현상금이 배정되지 않아 0원으로 판정한다 — CriminalAssigner 배정을 타지 않은 NPC인지 확인할 것", npc);

        return identity.Bounty;
    }

    private static void LogVerdict(ArrestResult result)
    {
        string citizenName = result.Profile != null ? result.Profile.CitizenName : result.Npc.name;
        string tag = result.Verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거",
            ArrestVerdict.Misdemeanor => "경범죄 처리",
            ArrestVerdict.ConditionUnmet => "생포 조건 불충족",
            _ => "오검거"
        };
        string deliverer = result.DeliveredBy.Count > 0
            ? string.Join(", ", result.DeliveredBy.ConvertAll(e => e.name))
            : "알 수 없음";
        Debug.Log($"[검거 판정] {tag}: {citizenName} (인계: {deliverer}) — 보상 {result.Reward}원");
    }
}

public readonly struct ArrestResult
{
    public readonly NpcController Npc;
    public readonly ArrestVerdict Verdict;
    public readonly CitizenProfile Profile;
    public readonly int Reward;

    public readonly List<PlayerEscorter> DeliveredBy;

    public readonly bool IsFirstDelivery;

    public ArrestResult(NpcController npc, ArrestVerdict verdict, CitizenProfile profile,
        int reward, List<PlayerEscorter> deliveredBy, bool isFirstDelivery)
    {
        Npc = npc;
        Verdict = verdict;
        Profile = profile;
        Reward = reward;
        DeliveredBy = deliveredBy ?? new List<PlayerEscorter>();
        IsFirstDelivery = isFirstDelivery;
    }
}