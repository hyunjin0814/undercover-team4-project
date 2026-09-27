#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 상점 없이 테스트 장비를 지급·회수하는 에디터 전용 단축키 — ; 부족분 지급, ' 전량 회수.
/// 호스트에서만 동작하며 접속한 전원에게 지급한다.
/// </summary>
public class ItemGrantDevHotkeys : MonoBehaviour
{
    private static readonly string[] k_defaultGearPaths =
    {
        "Assets/Prefabs/Items/Taser.prefab",
        "Assets/Prefabs/Items/Scanner.prefab",
        "Assets/Prefabs/Items/ReviveKit.prefab",
        "Assets/Prefabs/Items/HomeRunBaton.prefab",
        "Assets/Prefabs/Items/Rope.prefab",
    };

    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("지급 구성에서 지금 없는 것만 채워 준다")]
    [SerializeField] private Key m_grantKey = Key.Semicolon;

    [Tooltip("보유 아이템을 전량 회수한다 — 빈손에서 다시 시작할 때")]
    [SerializeField] private Key m_clearKey = Key.Quote;

    [Header("지급 구성")]
    [Tooltip("지급할 아이템 프리팹. 비우면 기본 세트를 Assets/Prefabs/Items에서 자동으로 찾는다")]
    [SerializeField] private List<ItemBase> m_gear = new List<ItemBase>();

    [Tooltip("켜면(기본) 접속한 전원에게 지급한다 — MPPM 클론도 같이 받아 원격 오너 경로를 볼 수 있다.\n\n" +
             "끄면 호스트 자신에게만 지급한다")]
    [SerializeField] private bool m_grantToEveryone = true;

    private readonly List<ItemBase> m_held = new List<ItemBase>();

    private List<ItemBase> m_defaultGear;

    private void Update()
    {
        if (!m_enabled)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_grantKey].wasPressedThisFrame)
            GrantToTargets();
        if (keyboard[m_clearKey].wasPressedThisFrame)
            ClearTargets();
    }

    private void GrantToTargets()
    {
        if (!TryCollectTargets(out List<PlayerLoadout> targets))
            return;

        List<ItemBase> gear = ResolveGear();
        if (gear.Count == 0)
        {
            Debug.LogWarning("[아이템/개발용] 지급할 프리팹이 없다 — 인스펙터의 '지급 구성'을 확인할 것", this);
            return;
        }

        foreach (PlayerLoadout loadout in targets)
            Grant(loadout, gear);
    }

    private void ClearTargets()
    {
        if (!TryCollectTargets(out List<PlayerLoadout> targets))
            return;

        foreach (PlayerLoadout loadout in targets)
        {
            PlayerItemSupply supply = loadout.GetComponent<PlayerItemSupply>();
            if (supply == null)
            {
                Debug.LogWarning($"[아이템/개발용] {loadout.name}에 PlayerItemSupply가 없다", loadout);
                continue;
            }

            supply.ServerClearHeldItems();
        }

        Debug.Log($"[아이템/개발용] {m_clearKey} — 보유 아이템 회수 ({targets.Count}명)");
    }

    private void Grant(PlayerLoadout loadout, List<ItemBase> gear)
    {
        HeldItems held = loadout.Held;

        m_held.Clear();
        held.CollectInto(m_held);

        int granted = 0;
        foreach (ItemBase gearPrefab in gear)
        {
            if (gearPrefab == null)
                continue;

            if (HoldsSameKind(gearPrefab))
                continue;

            if (held.Count >= PlayerLoadout.k_maxHeldItems)
            {
                Debug.LogWarning(
                    $"[아이템/개발용] {loadout.name}의 소지 한도({PlayerLoadout.k_maxHeldItems})가 찼다 — "
                        + $"{m_clearKey}로 비우고 다시 누를 것",
                    loadout
                );
                break;
            }

            ItemBase item = Instantiate(gearPrefab);
            NetworkObject itemNetworkObject = item.GetComponent<NetworkObject>();
            itemNetworkObject.SpawnWithOwnership(loadout.OwnerClientId);
            held.Attach(itemNetworkObject);
            m_held.Add(item);
            granted++;
        }

        if (granted == 0)
        {
            Debug.Log($"[아이템/개발용] {loadout.name} — 이미 다 들고 있다 (추가 지급 없음)", loadout);
            return;
        }

        loadout.ServerNotifyHeldItemsChanged();
        Debug.Log(
            $"[아이템/개발용] {m_grantKey} — {loadout.name}(client {loadout.OwnerClientId})에게 {granted}개 지급",
            loadout
        );
    }

    private bool HoldsSameKind(ItemBase gearPrefab)
    {
        for (int i = 0; i < m_held.Count; i++)
            if (m_held[i] != null && m_held[i].GetType() == gearPrefab.GetType())
                return true;

        return false;
    }

    private bool TryCollectTargets(out List<PlayerLoadout> targets)
    {
        targets = new List<PlayerLoadout>();

        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
        {
            Debug.LogWarning("[아이템/개발용] 세션이 없다 — 호스트가 뜬 뒤에 누를 것", this);
            return false;
        }

        if (!manager.IsServer)
            return false;

        if (m_grantToEveryone)
        {
            foreach (NetworkClient client in manager.ConnectedClientsList)
                AddTarget(targets, client.PlayerObject);
        }
        else
        {
            AddTarget(targets, manager.LocalClient?.PlayerObject);
        }

        if (targets.Count == 0)
        {
            Debug.LogWarning("[아이템/개발용] 지급 대상 플레이어를 찾지 못했다 — 라운드가 시작됐는지 확인할 것", this);
            return false;
        }

        return true;
    }

    private static void AddTarget(List<PlayerLoadout> into, NetworkObject playerObject)
    {
        if (playerObject == null)
            return;

        PlayerLoadout loadout = playerObject.GetComponent<PlayerLoadout>();
        if (loadout != null)
            into.Add(loadout);
    }

    private List<ItemBase> ResolveGear()
    {
        if (m_gear.Count > 0)
            return m_gear;

        if (m_defaultGear != null)
            return m_defaultGear;

        m_defaultGear = new List<ItemBase>();
        foreach (string path in k_defaultGearPaths)
        {
            ItemBase prefab = AssetDatabase.LoadAssetAtPath<ItemBase>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[아이템/개발용] 기본 세트 프리팹을 못 찾았다: {path}", this);
                continue;
            }

            m_defaultGear.Add(prefab);
        }

        return m_defaultGear;
    }
}
#endif
