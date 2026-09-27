#if UNITY_EDITOR
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 개발자 단축키용 플레이어 조회 헬퍼(에디터 전용).
/// </summary>
public static class DevPlayerLookup
{
    /// <summary>이 피어의 플레이어 — 세션이 없으면(오프라인 Play) 씬에 하나뿐이다.</summary>
    public static Transform LocalPlayer()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager != null && manager.IsListening && manager.LocalClient?.PlayerObject != null)
            return manager.LocalClient.PlayerObject.transform;

        PlayerMovement player = Object.FindFirstObjectByType<PlayerMovement>();
        return player != null ? player.transform : null;
    }

    /// <summary>지정한 접속자의 플레이어 — 없으면 null.</summary>
    public static Transform ResolvePlayer(ulong clientId)
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager != null
            && manager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            && client.PlayerObject != null)
            return client.PlayerObject.transform;

        return null;
    }
}
#endif
