using UnityEngine;

/// <summary>
/// 좌표가 감옥 방 부피 안인지 판정하는 정적 유틸. 감옥이 없는 씬에서는 항상 false.
/// </summary>
public static class JailRoom
{
    public static bool HasRoom => Zone != null && Zone.HasRoomVolume;

    /// <summary>이 좌표가 감옥 방 안인가 — 감옥이 없거나 방 범위가 미배선이면 항상 false.</summary>
    public static bool Contains(Vector3 position)
    {
        return Zone != null && Zone.ContainsPoint(position);
    }

    /// <summary>방 안에서 걸어갈 수 있는 임의의 NavMesh 지점을 고른다. 실패하면 false.</summary>
    public static bool TryRandomPoint(int areaMask, out Vector3 point)
    {
        point = Vector3.zero;
        if (Zone == null)
            return false;

        Vector3 candidate = Zone.RandomPointInRoom();
        if (!UnityEngine.AI.NavMesh.SamplePosition(candidate, out UnityEngine.AI.NavMeshHit hit, k_snapRadius, areaMask))
            return false;

        point = hit.position;
        return true;
    }

    private const float k_snapRadius = 3f;

    private static JailZone Zone => App.Game.Jail;
}
