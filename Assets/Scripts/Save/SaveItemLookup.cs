using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세이브용 아이템 id로 프리팹 이름을 쓰고, DefaultNetworkPrefabs 명부에서 프리팹을 찾는다.
/// 프리팹 파일 이름을 바꾸면 그 아이템의 기존 세이브는 복원되지 않는다.
/// </summary>
public static class SaveItemLookup
{
    /// <summary>세이브에 적을 id — 프리팹 이름. null이면 빈 문자열.</summary>
    public static string GetId(ItemBase prefab) => prefab != null ? prefab.name : string.Empty;

    /// <summary>id(프리팹 이름)로 등록된 네트워크 프리팹을 찾는다. 없으면 null.</summary>
    public static ItemBase Find(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig == null || nm.NetworkConfig.Prefabs == null)
            return null;

        foreach (NetworkPrefab entry in nm.NetworkConfig.Prefabs.Prefabs)
        {
            GameObject prefab = entry?.Prefab;
            if (prefab == null || prefab.name != id)
                continue;

            ItemBase item = prefab.GetComponent<ItemBase>();
            if (item != null)
                return item;
        }

        return null;
    }
}
