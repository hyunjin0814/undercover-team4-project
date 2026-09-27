using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 수배 리스트 — 검거 대상을 서버 권위로 채우고 검거되면 지우며, NetworkList로 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class WantedListManager : NetworkedManagerBase
{
    private AppearanceAssigner Appearance => App.Game.Appearance;
    private ArrestJudge Judge => App.Game.ArrestJudge;

    private readonly NetworkList<WantedEntry> m_wanted = new NetworkList<WantedEntry>();

    private readonly Dictionary<ulong, WantedEntry> m_arrestedEntries = new Dictionary<ulong, WantedEntry>();

    public NetworkList<WantedEntry> Wanted => m_wanted;

    private readonly NetworkVariable<int> m_totalWanted = new NetworkVariable<int>();
    public int TotalWanted => m_totalWanted.Value;
    public event Action OnTotalWantedChanged;

    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        m_totalWanted.OnValueChanged += HandleTotalWantedChanged;

        if (IsServer)
        {
            m_wanted.Clear();
            m_totalWanted.Value = 0;
            m_arrestedEntries.Clear();

            if (Appearance != null)
                Appearance.OnMontageGenerated += HandleMontageGenerated;
            else
                Debug.LogWarning("WantedListManager: AppearanceAssigner를 찾지 못해 수배 항목을 등록할 수 없다", this);

            if (Judge != null)
            {
                Judge.OnArrestJudged += HandleArrestJudged;
                Judge.OnCorpseJudged += HandleArrestJudged;
            }
            else
                Debug.LogWarning("WantedListManager: ArrestJudge를 찾지 못해 검거 시 항목을 지울 수 없다", this);
        }

        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_totalWanted.OnValueChanged -= HandleTotalWantedChanged;

        if (Appearance != null)
            Appearance.OnMontageGenerated -= HandleMontageGenerated;

        if (Judge != null)
        {
            Judge.OnArrestJudged -= HandleArrestJudged;
            Judge.OnCorpseJudged -= HandleArrestJudged;
        }
    }

    private void HandleMontageGenerated(NpcController criminal, AppearanceProfile appearance, RevealedAxisSet revealedAxes)
    {
        if (criminal == null)
        {
            Debug.LogWarning("WantedListManager: 범인 NPC가 없어 수배 항목을 등록하지 못했다", this);
            return;
        }

        CitizenIdentity identity = criminal.GetComponent<CitizenIdentity>();
        CitizenProfile profile = identity != null ? identity.Profile : null;
        string wantedName = profile != null ? profile.CitizenName : criminal.name;

        AppearanceAssigner assigner = Appearance;

        m_wanted.Add(new WantedEntry
        {
            NpcId = criminal.NetworkObjectId,
            Name = wantedName.ToFixed64(),
            Appearance = appearance.Masked(revealedAxes),
            RevealedAxes = revealedAxes,
            Bounty = identity != null ? identity.Bounty : 0,
            Condition = identity != null ? identity.WantedCondition : default,
        });
        m_totalWanted.Value++;
        AppearanceDatabase database = assigner != null ? assigner.Database : null;
        string montageText = database != null ? database.BuildMontageText(appearance, revealedAxes) : "?";
        Debug.Log($"[수배] 등록: {wantedName} — \"{montageText}\" / 현상금 {(identity != null ? identity.Bounty : 0)}원 (현재 {m_wanted.Count}건)");
    }

    private void HandleArrestJudged(ArrestResult result)
    {
        bool resolved = result.Verdict == ArrestVerdict.WantedCriminal || result.Verdict == ArrestVerdict.ConditionUnmet;
        if (!resolved || result.Npc == null)
            return;

        RemoveByNpcId(result.Npc.NetworkObjectId);
    }

    private void HandleTotalWantedChanged(int previous, int current) => OnTotalWantedChanged?.Invoke();

    private void RemoveByNpcId(ulong npcId)
    {
        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != npcId) continue;

            WantedEntry removed = m_wanted[i];
            m_wanted.RemoveAt(i);
            m_arrestedEntries[npcId] = removed;
            Debug.Log($"[수배] 검거 완료로 제거: {removed.Name} (남은 {m_wanted.Count}건)");
            return;
        }
    }

    /// <summary>회수하지 못한 수배 항목을 지우지 않고 행방불명으로 표시한다. 서버 전용.</summary>
    public void MarkMissing(ulong npcId)
    {
        if (!IsSpawned || !IsServer)
            return;

        for (int i = 0; i < m_wanted.Count; i++)
        {
            if (m_wanted[i].NpcId != npcId || m_wanted[i].Missing) continue;

            WantedEntry entry = m_wanted[i];
            entry.Missing = true;
            m_wanted[i] = entry;
            Debug.Log($"[수배] 행방불명 처리: {entry.Name}");
            return;
        }
    }

    /// <summary>검거로 내렸던 수배 항목을 보관된 몽타주 그대로 되살린다(탈옥 전용). 서버 전용.</summary>
    public void ReinstateByNpcId(ulong npcId)
    {
        if (!IsSpawned || !IsServer)
            return;

        if (!m_arrestedEntries.TryGetValue(npcId, out WantedEntry entry))
        {
            Debug.LogWarning($"WantedListManager: 보관된 수배 항목이 없어 재등재하지 못했다 (NpcId {npcId})", this);
            return;
        }

        m_arrestedEntries.Remove(npcId);
        m_wanted.Add(entry);
        Debug.Log($"[수배] 탈출로 재등재: {entry.Name} (현재 {m_wanted.Count}건)");
    }
}
