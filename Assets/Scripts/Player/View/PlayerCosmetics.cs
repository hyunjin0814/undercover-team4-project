using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 로봇 색 — 서버가 스폰 시점에 정하고 전 피어가 BodyTint로 부위별 색을 칠한다.
/// </summary>
[RequireComponent(typeof(BodyTint))]
public class PlayerCosmetics : NetworkBehaviour
{
    [Tooltip("색 팔레트 — 로비 팔레트와 반드시 같은 에셋을 물릴 것")]
    [SerializeField] private PlayerColorPalette m_palette;

    private readonly NetworkVariable<PlayerColorSet> m_colors = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> m_assigned = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public PlayerColorSet Colors => m_colors.Value;

    private BodyTint m_tint;
    private readonly Color[] m_buffer = new Color[3];

    private void Awake() => m_tint = GetComponent<BodyTint>();

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_colors.Value = ResolveSpawnColors(out bool resolved);
            m_assigned.Value = resolved;
        }

        if (IsOwner)
        {
            CosmeticLoadout.OnPlayerColorChanged += HandleOwnerColorChanged;

            if (!IsServer)
                ReportColorsRpc(PlayerColorSet.FromSettings());
        }

        m_colors.OnValueChanged += HandleColorsChanged;
        m_assigned.OnValueChanged += HandleAssignedChanged;
        Apply();
    }

    /// <summary>명부에 기록된 이 플레이어의 색을 돌려준다. 없으면 로컬 설정을 쓴다.</summary>
    private PlayerColorSet ResolveSpawnColors(out bool resolved)
    {
        resolved = true;

        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.TryGetEntry(OwnerClientId, out LobbyPlayerEntry entry))
            return entry.Colors;

        if (IsOwner)
            return PlayerColorSet.FromSettings();

        Debug.LogWarning(
            $"[{nameof(PlayerCosmetics)}] 명부에 색이 아직 없어 기본색으로 스폰했다 "
                + $"— 클라 {OwnerClientId}, 명부 {(App.Game.Roster == null ? "없음" : App.Game.Roster.Players.Count + "명")} (#790)",
            this
        );
        resolved = false;
        return default;
    }

    public override void OnNetworkDespawn()
    {
        m_colors.OnValueChanged -= HandleColorsChanged;
        m_assigned.OnValueChanged -= HandleAssignedChanged;

        CosmeticLoadout.OnPlayerColorChanged -= HandleOwnerColorChanged;
    }

    private void HandleOwnerColorChanged(EBodyPart _)
    {
        if (!IsOwner || !IsSpawned)
            return;

        PlayerColorSet colors = PlayerColorSet.FromSettings();
        if (IsServer)
            m_colors.Value = colors;
        else
            ReportColorsRpc(colors);
    }

    [Rpc(SendTo.Server)]
    private void ReportColorsRpc(PlayerColorSet colors)
    {
        m_colors.Value = colors;
        m_assigned.Value = true;
    }

    private void HandleAssignedChanged(bool previous, bool current) => Apply();

    private void HandleColorsChanged(PlayerColorSet previous, PlayerColorSet current) => Apply();

    private void Apply()
    {
        if (m_palette == null)
        {
            Debug.LogWarning($"[{nameof(PlayerCosmetics)}] 색 팔레트가 연결되지 않았습니다 (#432)", this);
            return;
        }

        PlayerColorSet colors = m_colors.Value;

        if (!m_assigned.Value)
        {
            if (!IsOwner)
            {
                m_tint.SetRendering(false);
                return;
            }

            colors = PlayerColorSet.FromSettings();
        }

        m_tint.SetRendering(true);

        for (int i = 0; i < m_buffer.Length; i++)
            m_buffer[i] = m_palette.Get(colors[(EBodyPart)i]);

        m_tint.SetBase(m_buffer, m_buffer[(int)EBodyPart.Torso]);
    }
}
