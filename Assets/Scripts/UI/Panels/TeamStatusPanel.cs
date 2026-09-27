using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tab 홀드 중 대원 전원의 상태와 이번 라운드 수배 몽타주를 표시하는 팀 상황판(커서·ESC 스택 미사용).
/// </summary>
public class TeamStatusPanel : PanelBase
{
    public override bool CanCloseWithESC => false;

    public override bool IsStackable => false;

    private const string k_table = "HudTable";

    [Header("머리말")]
    [SerializeField] private TextMeshProUGUI m_titleText;
    [SerializeField] private TextMeshProUGUI m_membersLabel;
    [SerializeField] private TextMeshProUGUI m_montageLabel;

    [Header("파티원")]
    [SerializeField] private RectTransform m_rowContainer;
    [SerializeField] private TeamStatusRowView m_rowPrefab;

    private readonly List<TeamStatusRowView> m_rows = new List<TeamStatusRowView>();
    private readonly List<NetworkObject> m_players = new List<NetworkObject>();

    private readonly List<NetworkObject> m_scratch = new List<NetworkObject>();

    private NetworkObject m_mine;

    private readonly List<PlayerHealth> m_health = new List<PlayerHealth>();
    private readonly List<PlayerIncapacitation> m_incapacitation = new List<PlayerIncapacitation>();
    private readonly List<PlayerNameTag> m_nameTags = new List<PlayerNameTag>();
    private readonly List<PlayerCosmetics> m_cosmetics = new List<PlayerCosmetics>();
    private readonly List<PlayerAccessories> m_accessories = new List<PlayerAccessories>();

    public override void OpenPanel()
    {
        ApplyLabels();
        Rebuild();
        base.OpenPanel();
    }

    private void ApplyLabels()
    {
        if (m_titleText != null)
            m_titleText.text = LocalizedStrings.Get(k_table, "Hud.Team.Title");

        if (m_membersLabel != null)
            m_membersLabel.text = LocalizedStrings.Get(k_table, "Hud.Team.Section.Members");

        if (m_montageLabel != null)
            m_montageLabel.text = LocalizedStrings.Get(k_table, "Hud.Team.Section.Wanted");
    }

    private void Update()
    {
        if (!IsOpened)
            return;

        if (CollectPlayers())
            Rebuild();
        else
            RefreshRows();
    }

    private bool CollectPlayers()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || manager.SpawnManager == null)
        {
            bool had = m_players.Count > 0;
            m_players.Clear();
            m_mine = null;
            return had;
        }

        IReadOnlyList<NetworkObject> spawned = manager.SpawnManager.PlayerObjects;
        NetworkObject mine = manager.LocalClient != null ? manager.LocalClient.PlayerObject : null;
        m_mine = mine;

        m_scratch.Clear();
        for (int i = 0; i < spawned.Count; i++)
        {
            NetworkObject player = spawned[i];
            if (player == null)
                continue;

            if (player == mine)
                m_scratch.Insert(0, player);
            else
                m_scratch.Add(player);
        }

        bool changed = m_scratch.Count != m_players.Count;

        if (!changed)
        {
            for (int i = 0; i < m_scratch.Count; i++)
            {
                if (m_scratch[i] == m_players[i])
                    continue;

                changed = true;
                break;
            }
        }

        if (!changed)
            return false;

        m_players.Clear();
        m_players.AddRange(m_scratch);

        return true;
    }

    private void Rebuild()
    {
        CollectPlayers();

        m_health.Clear();
        m_incapacitation.Clear();
        m_nameTags.Clear();
        m_cosmetics.Clear();
        m_accessories.Clear();
        for (int i = 0; i < m_players.Count; i++)
        {
            NetworkObject player = m_players[i];
            m_health.Add(player != null ? player.GetComponent<PlayerHealth>() : null);
            m_incapacitation.Add(player != null ? player.GetComponent<PlayerIncapacitation>() : null);
            m_nameTags.Add(player != null ? player.GetComponent<PlayerNameTag>() : null);
            m_cosmetics.Add(player != null ? player.GetComponent<PlayerCosmetics>() : null);
            m_accessories.Add(player != null ? player.GetComponent<PlayerAccessories>() : null);
        }

        if (m_rowContainer == null || m_rowPrefab == null)
            return;

        while (m_rows.Count < m_players.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        for (int i = 0; i < m_rows.Count; i++)
        {
            bool used = i < m_players.Count;
            m_rows[i].gameObject.SetActive(used);
            if (!used)
                continue;

            m_rows[i].SetName(NameOf(i));

            m_rows[i].SetPortrait(PortraitOf(i));

            m_rows[i].SetOwnership(m_mine != null && m_players[i] == m_mine);
        }

        RefreshRows();
    }

    private Texture PortraitOf(int index)
    {
        PlayerCosmetics cosmetics = index < m_cosmetics.Count ? m_cosmetics[index] : null;
        if (cosmetics == null)
            return null;

        PlayerAccessories accessories = index < m_accessories.Count ? m_accessories[index] : null;
        return LobbyPortraitStage.GetSessionPortrait(
            cosmetics.Colors,
            accessories != null ? accessories.Accessories : default
        );
    }

    private void RefreshRows()
    {
        for (int i = 0; i < m_players.Count && i < m_rows.Count; i++)
        {
            if (m_players[i] == null)
                continue;

            PlayerHealth health = i < m_health.Count ? m_health[i] : null;
            int max = health != null ? health.MaxHp : 0;
            int hp = health != null ? health.CurrentHp : 0;

            m_rows[i].SetStatus(hp, max, StateOf(i < m_incapacitation.Count ? m_incapacitation[i] : null));

            if (!m_rows[i].HasName)
                m_rows[i].SetName(NameOf(i));

            if (!m_rows[i].HasPortrait)
                m_rows[i].SetPortrait(PortraitOf(i));
        }
    }

    private string NameOf(int index)
    {
        PlayerNameTag tag = index < m_nameTags.Count ? m_nameTags[index] : null;
        return tag != null ? tag.DisplayName : string.Empty;
    }

    private static ETeamMemberState StateOf(PlayerIncapacitation incapacitation)
    {
        if (incapacitation == null)
            return ETeamMemberState.Alive;

        switch (incapacitation.Cause)
        {
            case IncapacitationCause.Die:
                return ETeamMemberState.Dead;

            case IncapacitationCause.Abducted:
            case IncapacitationCause.Beamed:
                return ETeamMemberState.Abducted;

            default:
                return ETeamMemberState.Alive;
        }
    }
}
