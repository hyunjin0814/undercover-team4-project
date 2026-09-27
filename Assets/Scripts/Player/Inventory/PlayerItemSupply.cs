using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 라운드 경계의 기본 장비 지급·회수. 서버 전용.
/// 소지품 집합과 오너 통지는 PlayerLoadout의 것을 빌려 쓴다.
/// </summary>
[RequireComponent(typeof(PlayerLoadout))]
public class PlayerItemSupply : NetworkBehaviour
{
    [Header("기본 지급 장비")]
    [Tooltip("게임 시작 시 순서대로 지급할 아이템 프리팹(NetworkObject). 첫 항목이 기본 장착된다.")]
    [SerializeField]
    private List<ItemBase> m_startingGear = new List<ItemBase>();

    [Header("상점 지급 장비 (#843)")]
    [Tooltip("상점 씬에서 지급할 아이템 프리팹 — 장비 카탈로그. 게임 씬 진입 시 회수된다.")]
    [SerializeField]
    private List<ItemBase> m_shopGear = new List<ItemBase>();

    private PlayerLoadout m_loadout;

    private void Awake()
    {
        m_loadout = GetComponent<PlayerLoadout>();
    }

    public override void OnNetworkSpawn()
    {
        if (App.CurrentScene == EScene.Game)
        {
            ServerGrantStartingGear();
        }
    }

    /// <summary>게임 씬 진입 시 기본 장비를 지급한다. 서버 판정은 한 프레임 뒤에 한다.</summary>
    public void ServerGrantStartingGear() => GrantAsync(m_startingGear, "기본 장비").Forget();

    /// <summary>상점 씬 진입 시 장비 카탈로그 아이템을 지급한다. 서버 전용.</summary>
    public void ServerGrantShopGear() => GrantAsync(m_shopGear, "상점 장비").Forget();

    private async UniTaskVoid GrantAsync(List<ItemBase> gear, string label)
    {
        await UniTask.NextFrame();

        if (this == null || !IsSpawned || !IsServer)
        {
            return;
        }

        Grant(gear, label);
    }

    private void Grant(List<ItemBase> gear, string label)
    {
        HeldItems held = m_loadout.Held;

        if (held.Count > 0)
        {
            Debug.LogWarning($"[PlayerItemSupply] 이미 아이템을 보유 중이라 {label} 지급을 건너뛴다.", this);
            return;
        }

        int granted = 0;
        foreach (ItemBase gearPrefab in gear)
        {
            if (gearPrefab == null)
            {
                continue;
            }

            if (granted >= PlayerLoadout.k_maxHeldItems)
            {
                Debug.LogWarning(
                    $"[PlayerItemSupply] {label} 지급이 소지 한도({PlayerLoadout.k_maxHeldItems})를 초과 — 초과분 무시. 지급 목록 설정 확인."
                );
                break;
            }

            ItemBase item = Instantiate(gearPrefab);
            NetworkObject itemNetworkObject = item.GetComponent<NetworkObject>();
            itemNetworkObject.SpawnWithOwnership(OwnerClientId);
            held.Attach(itemNetworkObject);
            granted++;
        }

        Debug.Log($"[PlayerItemSupply] {label} 지급 — client {OwnerClientId}, {granted}개 ({App.CurrentScene})");
        m_loadout.ServerNotifyHeldItemsChanged();
    }

    /// <summary>상점 복귀 시 보유 아이템을 전량 회수(디스폰)한다. 서버 전용.</summary>
    public void ServerClearHeldItems()
    {
        if (!IsServer)
        {
            return;
        }

        int cleared = m_loadout.Held.DespawnAll();
        Debug.Log($"[PlayerItemSupply] 보유 아이템 회수 — client {OwnerClientId}, {cleared}개");

        m_loadout.ServerNotifyHeldItemsChanged();
    }
}
