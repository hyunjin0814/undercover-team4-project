using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public struct SettlementData
{
    public RoundResult Result;
    public RoundEndReason Reason;
    public int FundBalance;
    public int FundDelta;
    public int GrossEarned;
    public int TargetFund;
    public int CriminalCount;
    public int MisdemeanorCount;
    public List<SettlementPlayerTitle> PlayerTitles;
}

public struct SettlementPlayerTitle
{
    public ulong ClientId;
    public string PlayerName;
    public SettlementTitleKind Title;
}

/// <summary>
/// 라운드 종료 시 결과·팀 자금 증감·개인 칭호를 모아 전 클라이언트 정산 패널에 띄운다(GDD 3-2).
/// 서버가 스냅샷을 네임드 메시지로 보내고 클라는 받아 표시한다.
/// </summary>
public class SettlementController : MonoBehaviour
{
    private const string k_messageName = "RoundSettlement";
    private const int k_writerSize = 800;
    private const int k_personalSharePercent = 10;

    private RoundManager Round => App.Game.Round;
    private TeamFund TeamFund => App.Game.TeamFund;

    private int m_roundStartFund;

    private void OnEnable()
    {
        if (Round != null)
        {
            Round.OnRoundStarted += HandleRoundStarted;
            Round.OnRoundEnded += HandleRoundEnded;
        }
    }

    private void OnDisable()
    {
        if (Round != null)
        {
            Round.OnRoundStarted -= HandleRoundStarted;
            Round.OnRoundEnded -= HandleRoundEnded;
        }
    }

    private void HandleRoundStarted()
    {
        m_roundStartFund = TeamFund != null ? TeamFund.Balance : 0;
    }

    private NamedMessageSubscription m_message;

    private void Start()
    {
        m_message = new NamedMessageSubscription(k_messageName, ReceiveSettlement);
        m_message.Attach();
    }

    private void OnDestroy() => m_message?.Detach();

    private void HandleRoundEnded(RoundResult result, RoundEndReason reason)
    {
        GatherAndShowAsync(result, reason).Forget();
    }

    private async UniTaskVoid GatherAndShowAsync(RoundResult result, RoundEndReason reason)
    {
        try
        {
            await UniTask.NextFrame(destroyCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SettlementData data = BuildData(result, reason);
        ShowLocal(data);
        Broadcast(data);
    }

    private SettlementData BuildData(RoundResult result, RoundEndReason reason)
    {
        int criminals = 0;
        int misdemeanors = 0;
        int gross = 0;
        JailZone jail = App.Game.Jail;
        if (jail != null)
            (criminals, misdemeanors, gross) = jail.TallySettlement();

        int target = Round != null ? Round.TargetFund : 0;
        int payout = Mathf.Max(0, gross - target);
        if (TeamFund != null)
            TeamFund.AddSettlement(payout);

        if (jail != null)
            PayPersonalShares(jail);

        int balance = TeamFund != null ? TeamFund.Balance : 0;
        int delta = TeamFund != null ? balance - m_roundStartFund : 0;

        return new SettlementData
        {
            Result = result,
            Reason = reason,
            FundBalance = balance,
            FundDelta = delta,
            GrossEarned = gross,
            TargetFund = target,
            CriminalCount = criminals,
            MisdemeanorCount = misdemeanors,
            PlayerTitles = BuildPlayerTitles(),
        };
    }

    private static List<SettlementPlayerTitle> BuildPlayerTitles()
    {
        IReadOnlyDictionary<ulong, int> arrests = App.Game.ArrestJudge?.PerPlayerArrests;
        IReadOnlyDictionary<ulong, int> offenses = App.Game.WrongfulArrestPenalty?.PerPlayerCounts;
        Dictionary<ulong, int> innocentKills = CollectPerPlayer(p => p.GetComponent<PlayerKillCredit>()?.InnocentKillCount ?? 0);
        Dictionary<ulong, int> downs = CollectPerPlayer(p => p.GetComponent<PlayerIncapacitation>()?.DownCount ?? 0);
        Dictionary<ulong, int> rescues = CollectPerPlayer(p => p.GetComponent<PlayerAssistCredit>()?.RescueCount ?? 0);

        bool hasArrest = TryFindTop(arrests, out ulong arrestWinner, out _);
        bool hasOffense = TryFindTop(offenses, out ulong offenseWinner, out _);
        bool hasInnocent = TryFindTop(innocentKills, out ulong innocentWinner, out _);
        bool hasDowns = TryFindTop(downs, out ulong downWinner, out _);
        bool hasRescue = TryFindTop(rescues, out ulong rescueWinner, out _);

        var roster = new HashSet<ulong>();
        AddConnectedIds(roster);
        AddKeys(roster, arrests);
        AddKeys(roster, offenses);
        AddKeys(roster, innocentKills);
        AddKeys(roster, downs);
        AddKeys(roster, rescues);

        var titles = new List<SettlementPlayerTitle>();
        foreach (ulong clientId in roster)
        {
            SettlementTitleKind kind = SettlementTitleKind.None;
            if (hasArrest && clientId == arrestWinner)
                kind = SettlementTitleKind.TopArrester;
            else if (hasOffense && clientId == offenseWinner)
                kind = SettlementTitleKind.TopOffender;
            else if (hasInnocent && clientId == innocentWinner)
                kind = SettlementTitleKind.TopInnocentKiller;
            else if (hasDowns && clientId == downWinner)
                kind = SettlementTitleKind.TopDowns;
            else if (hasRescue && clientId == rescueWinner)
                kind = SettlementTitleKind.TopRescuer;

            titles.Add(new SettlementPlayerTitle
            {
                ClientId = clientId,
                PlayerName = ResolvePlayerName(clientId),
                Title = kind,
            });
        }

        return titles;
    }

    private static Dictionary<ulong, int> CollectPerPlayer(Func<NetworkObject, int> selector)
    {
        var result = new Dictionary<ulong, int>();
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return result;

        foreach (KeyValuePair<ulong, NetworkClient> pair in nm.ConnectedClients)
        {
            NetworkObject player = pair.Value.PlayerObject;
            if (player == null)
                continue;

            int value = selector(player);
            if (value > 0)
                result[pair.Key] = value;
        }
        return result;
    }

    private static void AddConnectedIds(HashSet<ulong> ids)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;
        foreach (ulong clientId in nm.ConnectedClients.Keys)
            ids.Add(clientId);
    }

    private static void AddKeys(HashSet<ulong> ids, IReadOnlyDictionary<ulong, int> dict)
    {
        if (dict == null)
            return;
        foreach (ulong key in dict.Keys)
            ids.Add(key);
    }

    private static void PayPersonalShares(JailZone jail)
    {
        foreach (KeyValuePair<ulong, int> pair in jail.TallyDelivererCredits())
        {
            int share = pair.Value * k_personalSharePercent / 100;
            if (share <= 0)
                continue;

            PlayerWallet wallet = PlayerWallet.FindByClientId(pair.Key);
            if (wallet != null)
                wallet.ServerAdd(share);
        }
    }

    private static bool TryFindTop(IReadOnlyDictionary<ulong, int> counts, out ulong clientId, out int count)
    {
        clientId = 0;
        count = 0;
        if (counts == null)
            return false;

        foreach (KeyValuePair<ulong, int> pair in counts)
        {
            if (pair.Value <= count)
                continue;
            count = pair.Value;
            clientId = pair.Key;
        }
        return count > 0;
    }

    private static string ResolvePlayerName(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (
            nm != null
            && nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            && client.PlayerObject != null
        )
        {
            PlayerNameTag tag = client.PlayerObject.GetComponent<PlayerNameTag>();
            if (tag != null && !string.IsNullOrEmpty(tag.DisplayName))
                return tag.DisplayName;
        }
        return $"플레이어 {clientId}";
    }

    private void Broadcast(SettlementData data)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        List<SettlementPlayerTitle> titles = data.PlayerTitles ?? new List<SettlementPlayerTitle>();

        using FastBufferWriter writer = new FastBufferWriter(k_writerSize, Allocator.Temp);
        writer.WriteValueSafe((byte)data.Result);
        writer.WriteValueSafe((byte)data.Reason);
        writer.WriteValueSafe(data.FundBalance);
        writer.WriteValueSafe(data.FundDelta);
        writer.WriteValueSafe(data.GrossEarned);
        writer.WriteValueSafe(data.TargetFund);
        writer.WriteValueSafe(data.CriminalCount);
        writer.WriteValueSafe(data.MisdemeanorCount);

        writer.WriteValueSafe((byte)titles.Count);
        foreach (SettlementPlayerTitle title in titles)
        {
            writer.WriteValueSafe(title.ClientId);
            writer.WriteValueSafe((byte)title.Title);
            writer.WriteValueSafe(title.PlayerName.ToFixed64());
        }

        nm.CustomMessagingManager.SendNamedMessageToAll(
            k_messageName,
            writer,
            NetworkDelivery.Reliable
        );
    }

    private void ReceiveSettlement(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            return;

        reader.ReadValueSafe(out byte resultByte);
        reader.ReadValueSafe(out byte reasonByte);
        reader.ReadValueSafe(out int balance);
        reader.ReadValueSafe(out int delta);
        reader.ReadValueSafe(out int gross);
        reader.ReadValueSafe(out int target);
        reader.ReadValueSafe(out int criminals);
        reader.ReadValueSafe(out int misdemeanors);

        reader.ReadValueSafe(out byte titleCount);
        var titles = new List<SettlementPlayerTitle>(titleCount);
        for (int i = 0; i < titleCount; i++)
        {
            reader.ReadValueSafe(out ulong clientId);
            reader.ReadValueSafe(out byte titleByte);
            reader.ReadValueSafe(out FixedString64Bytes name);
            titles.Add(new SettlementPlayerTitle
            {
                ClientId = clientId,
                Title = (SettlementTitleKind)titleByte,
                PlayerName = name.ToString(),
            });
        }

        ShowLocal(
            new SettlementData
            {
                Result = (RoundResult)resultByte,
                Reason = (RoundEndReason)reasonByte,
                FundBalance = balance,
                FundDelta = delta,
                GrossEarned = gross,
                TargetFund = target,
                CriminalCount = criminals,
                MisdemeanorCount = misdemeanors,
                PlayerTitles = titles,
            }
        );
    }

    private static void ShowLocal(SettlementData data)
    {
        GrantCosmeticToken(data.Result);

        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SettlementPanel panel))
            panel.Show(data);
        else
            Debug.LogWarning("SettlementController: 정산 패널(SettlementPanel)을 찾지 못해 표시하지 못했다");
    }

    /// <summary>라운드를 클리어하면 각 피어가 자기 계정에 치장 뽑기 토큰 1개를 더한다.</summary>
    private static void GrantCosmeticToken(RoundResult result)
    {
        if (result != RoundResult.Success)
            return;

        CosmeticInventory.AddTokens(1);

        CosmeticInventory.QueueRewardNotice(1);

        Debug.Log($"[치장] 라운드 클리어 — 뽑기 토큰 +1 (보유 {CosmeticInventory.Tokens}개)");
    }
}
