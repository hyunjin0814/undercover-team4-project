using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 치장 — 서버가 스폰 시점에 인덱스를 정하고 전 피어가 각자 로컬로 부착물을 붙인다.
/// </summary>
public class PlayerAccessories : NetworkBehaviour
{
    [Tooltip("치장 카탈로그 — 로비 선택 칸과 반드시 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("부착 기준 본 — 3인칭 몸통 리그의 머리. 1인칭 팔 리그를 물리지 말 것")]
    [SerializeField] private Transform m_headBone;

    [Tooltip("레이어를 물려받을 3인칭 몸 렌더러 — 뼈가 아니라 이쪽을 따라간다 (Apply 주석 참고)")]
    [SerializeField] private Renderer m_bodyRenderer;

    private readonly NetworkVariable<AccessorySet> m_accessories = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public AccessorySet Accessories => m_accessories.Value;

    private readonly GameObject[] m_spawned = new GameObject[Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length];

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            m_accessories.Value = ResolveSpawnAccessories();

        if (IsOwner)
        {
            CosmeticInventory.SanitizeEquipped(m_catalog);

            CosmeticLoadout.OnAccessoryChanged += HandleOwnerAccessoryChanged;

            AccessorySet mine = AccessorySet.FromSettings();
            if (IsServer)
                m_accessories.Value = mine;
            else
                ReportAccessoriesRpc(mine);
        }

        m_accessories.OnValueChanged += HandleAccessoriesChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_accessories.OnValueChanged -= HandleAccessoriesChanged;

        CosmeticLoadout.OnAccessoryChanged -= HandleOwnerAccessoryChanged;
    }

    /// <summary>명부에 기록된 이 플레이어의 치장을 돌려준다. 없으면 로컬 설정이나 기본값을 쓴다.</summary>
    private AccessorySet ResolveSpawnAccessories()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.TryGetEntry(OwnerClientId, out LobbyPlayerEntry entry))
            return entry.Accessories;

        return IsOwner ? AccessorySet.FromSettings() : default;
    }

    private void HandleOwnerAccessoryChanged(EAccessorySlot _)
    {
        if (!IsOwner || !IsSpawned)
            return;

        AccessorySet set = AccessorySet.FromSettings();
        if (IsServer)
            m_accessories.Value = set;
        else
            ReportAccessoriesRpc(set);
    }

    [Rpc(SendTo.Server)]
    private void ReportAccessoriesRpc(AccessorySet set) => m_accessories.Value = set;

    private void HandleAccessoriesChanged(AccessorySet previous, AccessorySet current) => Apply();

    private int LayerSource() =>
        m_bodyRenderer != null ? m_bodyRenderer.gameObject.layer : gameObject.layer;

    private void Apply()
    {
        if (m_catalog == null || m_headBone == null)
        {
            Debug.LogWarning(
                $"[{nameof(PlayerAccessories)}] 카탈로그 또는 머리 본이 연결되지 않았습니다 (#818)",
                this
            );
            return;
        }

        AccessorySet set = m_accessories.Value;

        EAccessorySlotMask hidden = m_catalog.HiddenSlots(set);

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int i = (int)slot;

            if (m_spawned[i] != null)
            {
                Destroy(m_spawned[i]);
                m_spawned[i] = null;
            }

            GameObject prefab = AccessoryCatalog.IsHidden(hidden, slot)
                ? null
                : m_catalog.Get(slot, set[slot]);

            if (prefab == null)
                continue;

            m_spawned[i] = Instantiate(prefab, m_headBone, false);
            m_spawned[i].name = prefab.name;

            PlayerLook.SetLayerRecursively(m_spawned[i].transform, LayerSource());
        }
    }
}
