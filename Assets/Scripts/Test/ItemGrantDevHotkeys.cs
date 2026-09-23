#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 아이템 지급 개발자 단축키 — <b>에디터 전용</b>. 상점을 거치지 않고 테스트용 장비를 손에 쥐여 준다.
///
/// <list type="bullet">
/// <item><c>;</c> — 지급 구성에서 <b>지금 없는 것만</b> 채워 준다(멱등 — 여러 번 눌러도 안전)</item>
/// <item><c>'</c> — 보유 아이템 전량 회수</item>
/// </list>
///
/// <b>왜 필요한가</b> — 정식 지급 경로는 라운드 경계뿐이다: <see cref="PlayerItemSupply"/>의 기본 장비는
/// 프리팹에 고정이고, 그 밖의 장비는 상점 구매 → <see cref="ShopDelivery"/> 배달을 타야 한다. 맵 씬을
/// 직접 Play하는 <see cref="DevAutoHost"/> 흐름에는 상점이 없어 테이저·스캐너 같은 장비를 손에 넣을
/// 방법이 아예 없다.
///
/// <b>호스트(서버)에서만 듣는다</b> — 아이템 스폰은 서버 권위다. 기본값은 <b>접속한 전원에게 지급</b>이라
/// 호스트가 한 번 누르면 MPPM 클론도 같이 받는다. 클론에서 눌러도 아무 일도 일어나지 않는다 —
/// 개발용 키는 모두 같은 규칙을 따른다(<see cref="SuddenEventDevHotkeys"/>).
/// 클론이 스스로 요청하게 하려면 <see cref="BombDevHotkeys"/>처럼 서버 RPC가 필요하고, 그러려면 이
/// 컴포넌트가 <b>씬의 NetworkObject</b> 위에 있어야 한다 — 지금은 그 씬 변경을 피해 전원 지급으로 둔다.
///
/// 지급 절차는 <see cref="PlayerItemSupply"/>와 같다(스폰 → 오너 이전 → 부착 → 오너 동기화). 다른 점은
/// <b>보유 중인 것은 건너뛰고 빈 칸만 채운다</b>는 것뿐이다 — 그쪽은 중복 지급을 막으려고 "뭐라도 들고
/// 있으면 통째로 건너뛰기"인데, 여기서는 테스트 도중 필요한 것만 더 받는 쪽이 쓸모 있다.
///
/// 씬의 아무 오브젝트에나 붙이면 된다(<c>Test</c> 오브젝트의 <see cref="DevAutoHost"/> 옆이 제자리다).
/// 관례는 <see cref="RagdollWallDevHotkeys"/>와 같다 — <c>UNITY_EDITOR</c>로 감싸 빌드에서 사라지고,
/// 키보드가 없는 구성에서는 조용히 넘어간다.
/// </summary>
public class ItemGrantDevHotkeys : MonoBehaviour
{
    // 지급 구성을 비워 뒀을 때 쓰는 기본 세트 — 채널링 게이지가 붙은 장비 넷 + 밧줄로 소지 한도(5칸)에 맞다.
    // 에디터 전용이라 AssetDatabase로 직접 집는다: 인스펙터에 손으로 물리지 않아도 바로 쓰게 하려는 것이고,
    // 프리팹이 옮겨지면 조용히 실패하는 대신 어느 경로를 못 찾았는지 콘솔에 남긴다.
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
    [Tooltip("지급할 아이템 프리팹. 비워 두면 기본 세트(테이저·스캐너·부활키트·홈런봉·밧줄)를 " +
             "Assets/Prefabs/Items에서 자동으로 찾는다.\n\n" +
             "한 가지만 시험할 때는 여기에 그것만 남기면 된다")]
    [SerializeField] private List<ItemBase> m_gear = new List<ItemBase>();

    [Tooltip("켜면(기본) 접속한 전원에게 지급한다 — MPPM 클론도 같이 받아 원격 오너 경로를 볼 수 있다.\n\n" +
             "끄면 호스트 자신에게만 지급한다")]
    [SerializeField] private bool m_grantToEveryone = true;

    // 보유 목록 조회용 재사용 버퍼 — 키를 누를 때만 쓰므로 하나면 충분하다.
    private readonly List<ItemBase> m_held = new List<ItemBase>();

    // 인스펙터를 비워 뒀을 때 한 번만 찾아 둔 기본 세트.
    private List<ItemBase> m_defaultGear;

    private void Update()
    {
        if (!m_enabled)
            return;

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_grantKey].wasPressedThisFrame)
            GrantToTargets();
        if (keyboard[m_clearKey].wasPressedThisFrame)
            ClearTargets();
    }

    // ---- 처리 (서버 전용) ----

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
            // 회수는 정식 경로를 그대로 부른다 — 디스폰과 오너 슬롯 재구성이 한 묶음이다 (#370).
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

    // 없는 것만 채운다. 절차는 PlayerItemSupply.Grant와 같다 — 스폰 → 오너 이전 → 부착 → 오너 동기화.
    private void Grant(PlayerLoadout loadout, List<ItemBase> gear)
    {
        HeldItems held = loadout.Held;

        // 지금 들고 있는 것 — 같은 종류를 또 주지 않으려는 것이다. 프리팹과 인스턴스의 구체 타입이
        // 같으므로(Taser 프리팹 → Taser 인스턴스) 타입 비교로 가린다.
        m_held.Clear();
        held.CollectInto(m_held);

        int granted = 0;
        foreach (ItemBase gearPrefab in gear)
        {
            if (gearPrefab == null)
                continue;

            if (HoldsSameKind(gearPrefab))
                continue;

            // 소지 5칸 제한 (#144/#793) — 넘겨 스폰하면 슬롯에 못 들어간 채 보유 카운트만 차서
            // 이후 줍기가 전부 거부된다(PlayerItemSupply와 같은 이유).
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

        // 오너 인벤토리 재구성의 유일한 통로 — 빼먹으면 서버에만 붙고 오너 화면에는 안 보인다.
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

    // ---- 조회 ----

    // 지급 대상. 스폰은 서버 권위라 호스트에서만 성립한다 — 클론에서 눌렀거나 세션이 없으면 여기서 물러난다.
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
            return false; // 클론에서 누른 경우 — 조용히 무시한다(개발용 키 공통 규칙)

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

    // 인스펙터 구성이 우선. 비어 있으면 기본 세트를 한 번 찾아 기억한다.
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
